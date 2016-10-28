#r "packages/FAKE/tools/FakeLib.dll"

open System
open System.IO
open System.Text.RegularExpressions
open System.Collections.Generic
open Fake
open Fake.AssemblyInfoFile

module Util =
    open System.Net

    let (|RegexReplace|_|) =
        let cache = new Dictionary<string, Regex>()
        fun pattern (replacement: string) input ->
            let regex =
                match cache.TryGetValue(pattern) with
                | true, regex -> regex
                | false, _ ->
                    let regex = Regex pattern
                    cache.Add(pattern, regex)
                    regex
            let m = regex.Match(input)
            if m.Success
            then regex.Replace(input, replacement) |> Some
            else None

    let join pathParts =
        Path.Combine(Array.ofSeq pathParts)

    let run workingDir fileName args =
        printfn "CWD: %s" workingDir
        let fileName, args =
            if EnvironmentHelper.isUnix
            then fileName, args else "cmd", ("/C " + fileName + " " + args)
        let ok =
            execProcess (fun info ->
                info.FileName <- fileName
                info.WorkingDirectory <- workingDir
                info.Arguments <- args) TimeSpan.MaxValue
        if not ok then failwith (sprintf "'%s> %s %s' task failed" workingDir fileName args)

    let runAndReturn workingDir fileName args =
        printfn "CWD: %s" workingDir
        let fileName, args =
            if EnvironmentHelper.isUnix
            then fileName, args else "cmd", ("/C " + args)
        ExecProcessAndReturnMessages (fun info ->
            info.FileName <- fileName
            info.WorkingDirectory <- workingDir
            info.Arguments <- args) TimeSpan.MaxValue
        |> fun p -> p.Messages |> String.concat "\n"

    let downloadArtifact path (url: string) =
        let tempFile = Path.ChangeExtension(Path.GetTempFileName(), ".zip")
        use client = new WebClient()
        use stream = client.OpenRead(url)
        use writer = new StreamWriter(tempFile)
        stream.CopyTo(writer.BaseStream)
        FileUtils.mkdir path
        CleanDir path
        run path "unzip" (sprintf "-q %s" tempFile)
        File.Delete tempFile

    let rmdir dir =
        if EnvironmentHelper.isUnix
        then FileUtils.rm_rf dir
        // Use this in Windows to prevent conflicts with paths too long
        else run "." "cmd" ("/C rmdir /s /q " + Path.GetFullPath dir)

    let visitFile (visitor: string->string) (fileName : string) =
        File.ReadAllLines(fileName)
        |> Array.map (visitor)
        |> fun lines -> File.WriteAllLines(fileName, lines)

        // This code is supposed to prevent OutOfMemory exceptions but it outputs wrong BOM
        // use reader = new StreamReader(fileName, encoding)
        // let tempFileName = Path.GetTempFileName()
        // use writer = new StreamWriter(tempFileName, false, encoding)
        // while not reader.EndOfStream do
        //     reader.ReadLine() |> visitor |> writer.WriteLine
        // reader.Close()
        // writer.Close()
        // File.Delete(fileName)
        // File.Move(tempFileName, fileName)

    let compileScript symbols outDir fsxPath =
        let dllFile = Path.ChangeExtension(Path.GetFileName fsxPath, ".dll")
        let opts = [
            yield FscHelper.Out (Path.Combine(outDir, dllFile))
            yield FscHelper.Target FscHelper.TargetType.Library
            yield! symbols |> List.map FscHelper.Define
        ]
        FscHelper.compile opts [fsxPath]
        |> function 0 -> () | _ -> failwithf "Cannot compile %s" fsxPath

    let normalizeVersion (version: string) =
        let i = version.IndexOf("-")
        if i > 0 then version.Substring(0, i) else version

    let assemblyInfo projectDir version extra =
        let version = normalizeVersion version
        let asmInfoPath = projectDir </> "AssemblyInfo.fs"
        (Attribute.Version version)::extra
        |> CreateFSharpAssemblyInfo asmInfoPath

    let loadReleaseNotes pkg =
        Lazy<_>(fun () ->
            sprintf "RELEASE_NOTES_%s.md" pkg
            |> ReleaseNotesHelper.LoadReleaseNotes)

module Npm =
    let script workingDir script args =
        sprintf "run %s -- %s" script (String.concat " " args)
        |> Util.run workingDir "npm"

    let install workingDir modules =
        sprintf "install %s" (String.concat " " modules)
        |> Util.run workingDir "npm"

    let command workingDir command args =
        sprintf "%s %s" command (String.concat " " args)
        |> Util.run workingDir "npm"

    let commandAndReturn workingDir command args =
        sprintf "%s %s" command (String.concat " " args)
        |> Util.runAndReturn workingDir "npm"

    let getLatestVersion package tag =
        let package =
            match tag with
            | Some tag -> package + "@" + tag
            | None -> package
        commandAndReturn "." "show" [package; "version"]

    let updatePackageKeyValue f pkgDir keys =
        let pkgJson = Path.Combine(pkgDir, "package.json")
        let reg =
            String.concat "|" keys
            |> sprintf "\"(%s)\"\\s*:\\s*\"(.*?)\""
            |> Regex
        let lines =
            File.ReadAllLines pkgJson
            |> Array.map (fun line ->
                let m = reg.Match(line)
                if m.Success then
                    match f(m.Groups.[1].Value, m.Groups.[2].Value) with
                    | Some(k,v) -> reg.Replace(line, sprintf "\"%s\": \"%s\"" k v)
                    | None -> line
                else line)
        File.WriteAllLines(pkgJson, lines)

module Node =
    let run workingDir script args =
        let args = sprintf "%s %s" script (String.concat " " args)
        Util.run workingDir "node" args

module Fake =
    let fakePath = "packages" </> "docs" </> "FAKE" </> "tools" </> "FAKE.exe"
    let fakeStartInfo script workingDirectory args fsiargs environmentVars =
        (fun (info: System.Diagnostics.ProcessStartInfo) ->
            info.FileName <- System.IO.Path.GetFullPath fakePath
            info.Arguments <- sprintf "%s --fsiargs -d:FAKE %s \"%s\"" args fsiargs script
            info.WorkingDirectory <- workingDirectory
            let setVar k v = info.EnvironmentVariables.[k] <- v
            for (k, v) in environmentVars do setVar k v
            setVar "MSBuild" msBuildExe
            setVar "GIT" Git.CommandHelper.gitPath
            setVar "FSI" fsiPath)

    /// Run the given buildscript with FAKE.exe
    let executeFAKEWithOutput workingDirectory script fsiargs envArgs =
        let exitCode =
            ExecProcessWithLambdas
                (fakeStartInfo script workingDirectory "" fsiargs envArgs)
                TimeSpan.MaxValue false ignore ignore
        System.Threading.Thread.Sleep 1000
        exitCode

// version info
let releaseCompiler = Util.loadReleaseNotes "COMPILER"
let releaseCore = Util.loadReleaseNotes "CORE"

// Targets
Target "Clean" (fun _ ->
    // Don't delete node_modules for faster builds
    !! "build/fable/bin" ++ "src/fable/*/obj/"
    |> CleanDirs

    !! "build/fable/**/*.*" -- "build/fable/node_modules/**/*.*"
    |> Seq.iter FileUtils.rm
    !! "build/tests/**/*.*" -- "build/tests/node_modules/**/*.*"
    |> Seq.iter FileUtils.rm
)

let buildFableCompilerJs buildDir isNetcore =
    FileUtils.cp_r "src/fable/Fable.Client.Node/js" buildDir
    FileUtils.cp "README.md" buildDir
    Npm.command buildDir "version" [releaseCompiler.Value.NugetVersion]
    Npm.install buildDir []

    // Update constants.js
    let pkgVersion =
        releaseCompiler.Value.NugetVersion
        |> sprintf "PKG_VERSION = \"%s\""
    let pkgName =
        if isNetcore then "-netcore" else ""
        |> sprintf "PKG_NAME = \"fable-compiler%s\""
    buildDir </> "constants.js"
    |> Util.visitFile (function
        | Util.RegexReplace "PKG_VERSION\s*=\s\".*?\"" pkgVersion newLine -> newLine
        | Util.RegexReplace "PKG_NAME\s*=\s\".*?\"" pkgName newLine -> newLine
        | line -> line)

    // Update name and command in package.json if necessary
    if isNetcore then
        (buildDir, ["name"; "fable"])
        ||> Npm.updatePackageKeyValue (function
            | "name", _ -> Some("name", "fable-compiler-netcore")
            | "fable", v -> Some("fable-netcore", v)
            | _ -> None)

Target "FableCompilerRelease" (fun _ ->
    Util.assemblyInfo "src/fable/Fable.Core" releaseCore.Value.NugetVersion []
    Util.assemblyInfo "src/fable/Fable.Compiler" releaseCompiler.Value.NugetVersion []
    Util.assemblyInfo "src/fable/Fable.Client.Node" releaseCompiler.Value.NugetVersion [
        Attribute.Metadata ("fableCoreVersion", Util.normalizeVersion releaseCore.Value.NugetVersion)
    ]

    let buildDir = "build/fable"

    [ "src/fable/Fable.Core/Fable.Core.fsproj"
      "src/fable/Fable.Compiler/Fable.Compiler.fsproj"
      "src/fable/Fable.Client.Node/Fable.Client.Node.fsproj" ]
    |> MSBuildRelease (buildDir + "/bin") "Build"
    |> Log "Fable-Compiler-Release-Output: "

    // For some reason, ProjectCracker targets are not working after updating the package
    !! "packages/FSharp.Compiler.Service.ProjectCracker/utilities/net45/FSharp.Compiler.Service.ProjectCrackerTool.exe*"
    |> Seq.iter (fun x -> FileUtils.cp x "build/fable/bin")

    buildFableCompilerJs buildDir false
)

Target "FableCompilerDebug" (fun _ ->
    let buildDir = "build/fable"

    [ "src/fable/Fable.Core/Fable.Core.fsproj"
      "src/fable/Fable.Compiler/Fable.Compiler.fsproj"
      "src/fable/Fable.Client.Node/Fable.Client.Node.fsproj" ]
    |> MSBuildDebug (buildDir + "/bin") "Build"
    |> Log "Fable-Compiler-Debug-Output: "

    buildFableCompilerJs buildDir false
)

Target "FableCompilerNetcore" (fun _ ->
    try
        let buildDir = "build/fable"
        buildFableCompilerJs buildDir true

        // Restore packages
        Util.run "." "dotnet" "restore"

        // Publish Fable NetCore
        let srcDir = "src/netcore/Fable.Client.Node"
        Util.run srcDir "dotnet" "publish -c Release"
        FileUtils.cp_r (srcDir + "/bin/Release/netcoreapp1.0/publish") (buildDir + "/bin")

        // Put FSharp.Core.optdata/sigdata next to FSharp.Core.dll
        FileUtils.cp (buildDir + "/bin/runtimes/any/native/FSharp.Core.optdata") (buildDir + "/bin")
        FileUtils.cp (buildDir + "/bin/runtimes/any/native/FSharp.Core.sigdata") (buildDir + "/bin")

        // Compile NUnit plugin
        let pluginDir = "src/plugins/nunit"
        Util.run pluginDir "dotnet" "build -c Release"

        // Run dotnet tests
        let testDir = "src/tests"
        Util.run testDir "dotnet" "test -c Release"

        // Compile JavaScript tests
        Node.run "." buildDir ["src/tests --target netcore"]
        let testsBuildDir = "build/tests"
        FileUtils.cp "src/tests/package.json" testsBuildDir
        Npm.install testsBuildDir []

        // // Copy the development version of fable-core.js
        // let fableCoreNpmDir = "src/fable/Fable.Core/npm"
        // Npm.install fableCoreNpmDir []
        // Npm.script fableCoreNpmDir "tsc" ["fable-core.ts --target ES2015 --declaration"]
        // setEnvironVar "BABEL_ENV" "target-umd"
        // Npm.script fableCoreNpmDir "babel" ["fable-core.js -o fable-core.js --compact=false"]
        // FileUtils.cp "src/fable/Fable.Core/npm/fable-core.js" "build/tests/node_modules/fable-core/"

        // Compile Fable.Core TypeScript
        let fableCoreSrcDir = "src/fable/Fable.Core/ts"
        Npm.install fableCoreSrcDir []
        Npm.script fableCoreSrcDir "tsc" ["--module commonjs"]

        // Run JavaScript tests
        Npm.script testsBuildDir "test" []
    with
    | ex ->
        printfn "Target FableCompilerNetcore didn't work, make sure of the following:"
        printfn "- You have NetCore SDK installed"
        printfn "- You cloned FSharp.Compiler.Service on same level as Fable"
        printfn "- FSharp.Compiler.Service > build All.NetCore run successfully"
        raise ex
)

Target "NUnitTest" (fun _ ->
    let testsBuildDir = "build/tests"

    !! "src/tests/Main/Fable.Tests.fsproj"
    |> MSBuildRelease testsBuildDir "Build"
    |> ignore

    [Path.Combine(testsBuildDir, "Fable.Tests.dll")]
    |> Testing.NUnit3.NUnit3 id
)

let compileAndRunMochaTests es2015 =
    let testsBuildDir = "build/tests"
    let testCompileArgs =
        ["--verbose" + if es2015 then " --ecma es2015" else ""]

    Node.run "." "build/fable" ["src/tests/DllRef"]
    Node.run "." "build/fable" ("src/tests/"::testCompileArgs)
    FileUtils.cp "src/tests/package.json" testsBuildDir
    Npm.install testsBuildDir []
    Npm.script testsBuildDir "test" []

Target "MochaTest" (fun _ ->
    compileAndRunMochaTests false
)

Target "OnlyMochaTest" (fun _ ->
    compileAndRunMochaTests false
)

Target "ES6MochaTest" (fun _ ->
    compileAndRunMochaTests true
)

let quickTest _ =
    Node.run "." "build/Fable" ["src/tools/QuickTest.fsx -o src/tools/temp -m commonjs --coreLib ./build/fable-core --verbose"]
    Node.run "." "src/tools/temp/QuickTest.js" []

Target "QuickTest" quickTest

Target "QuickFableCompilerTest" quickTest

Target "QuickFableCoreTest" quickTest

Target "Plugins" (fun _ ->
    !! "src/plugins/**/*.fsx"
    |> Seq.iter (fun fsx -> Util.compileScript [] (Path.GetDirectoryName fsx) fsx)
)

Target "Providers" (fun _ ->
    !! "src/providers/**/*.fsx"
    |> Seq.filter (fun path -> path.Contains("test") |> not)
    |> Seq.iter (fun fsxPath ->
        let buildDir = Path.GetDirectoryName(Path.GetDirectoryName(fsxPath))
        Util.compileScript ["NO_GENERATIVE"] buildDir fsxPath)
)

Target "MakeArtifactLighter" (fun _ ->
    Util.rmdir "build/fable/node_modules"
    !! "build/fable/bin/*.pdb" ++ "build/fable/bin/*.xml"
    |> Seq.iter FileUtils.rm
)

Target "PublishCompiler" (fun _ ->
    let applyTag = function
        | Some tag -> ["--tag"; tag]
        | None -> []

    // Check if version is prerelease or not
    let fableCompilerTag =
        if releaseCompiler.Value.NugetVersion.IndexOf("-") > 0 then Some "next" else None

    let workingDir = "temp/build"
    let url = "https://ci.appveyor.com/api/projects/alfonsogarciacaro/fable/artifacts/build/fable.zip"
    Util.downloadArtifact workingDir url
    applyTag fableCompilerTag |> Npm.command workingDir "publish"
)

Target "PublishCore" (fun _ ->
    // Check if version is prerelease or not
    if releaseCore.Value.NugetVersion.IndexOf("-") > 0 then ["--tag next"] else []
    |> Npm.command "build/fable-core" "publish"
)

Target "PublishCompilerNetcore" (fun _ ->
    // Check if version is prerelease or not
    if releaseCompiler.Value.NugetVersion.IndexOf("-") > 0 then ["--tag next"] else []
    |> Npm.command "build/fable" "publish"
)

let publishNugetPackage pkg =
    let release =
        sprintf "src/nuget/%s/RELEASE_NOTES.md" pkg
        |> ReleaseNotesHelper.LoadReleaseNotes
    CleanDir <| sprintf "nuget/%s" pkg
    Paket.Pack(fun p ->
        { p with
            Version = release.NugetVersion
            OutputPath = sprintf "nuget/%s" pkg
            TemplateFile = sprintf "src/nuget/%s/%s.fsproj.paket.template" pkg pkg
            // IncludeReferencedProjects = true
        })
    Paket.Push(fun p ->
        { p with
            WorkingDir = sprintf "nuget/%s" pkg
            PublishUrl = "https://www.nuget.org/api/v2/package" })

Target "PublishJsonConverter" (fun _ ->
    let pkg = "Fable.JsonConverter"
    let pkgDir = "src" </> "nuget" </> pkg
    !! (pkgDir + "/*.fsproj")
    |> MSBuildRelease (pkgDir </> "bin" </> "Release") "Build"
    |> Log (pkg + ": ")
    publishNugetPackage pkg
)

Target "FableCoreRelease" (fun _ ->
    let fableCoreNpmDir = "build/fable-core"
    let fableCoreSrcDir = "src/fable/Fable.Core"

    // Don't delete node_moduels for faster builds
    !! (fableCoreNpmDir </> "**/*.*")
        -- (fableCoreNpmDir </> "node_modules/**/*.*")
    |> Seq.iter FileUtils.rm

    // Update Fable.Core version
    Util.assemblyInfo fableCoreSrcDir releaseCore.Value.NugetVersion []

    !! (fableCoreSrcDir </> "Fable.Core.fsproj")
    |> MSBuild fableCoreNpmDir "Build" [
        "Configuration","Release"
        "DebugSymbols", "False"
        "DebugType", "None"
        "DefineConstants","IMPORT"
        "DocumentationFile","../../../build/fable-core/Fable.Core.xml"]
    |> ignore // Log outputs all files in node_modules

    // Remove unneeded files
    !! (fableCoreNpmDir </> "*.*")
    |> Seq.iter (fun file ->
        let fileName = Path.GetFileName file
        if fileName <> "Fable.Core.dll" && fileName <> "Fable.Core.xml" then
            FileUtils.rm file
    )

    // Copy README and package.json
    FileUtils.cp (fableCoreSrcDir </> "ts/README.md") fableCoreNpmDir
    FileUtils.cp (fableCoreSrcDir </> "ts/package.json") fableCoreNpmDir
    FileUtils.cp (fableCoreSrcDir </> "ts/.babelrc") fableCoreNpmDir

    Npm.install fableCoreNpmDir []
    Npm.command fableCoreNpmDir "version" [releaseCore.Value.NugetVersion]

    // Compile TypeScript
    Npm.script fableCoreNpmDir "tsc" [sprintf "--project ../../%s/ts" fableCoreSrcDir]

    // Compile Es2015 syntax to ES5 with different module targets
    setEnvironVar "BABEL_ENV" "target-es2015"
    Npm.script fableCoreNpmDir "babel" [". --out-dir es2015 --compact=false"]
    setEnvironVar "BABEL_ENV" "target-commonjs"
    Npm.script fableCoreNpmDir "babel" [". --out-dir . --compact=false"]
)

Target "FableCoreDebug" (fun _ ->
    let fableCoreNpmDir = "build/fable-core"
    let fableCoreSrcDir = "src/fable/Fable.Core"

    !! (fableCoreSrcDir </> "Fable.Core.fsproj")
    |> MSBuild fableCoreNpmDir "Build" [
        "Configuration","Debug"
        "DefineConstants","IMPORT"]
    |> ignore // Log outputs all files in node_modules

    Npm.script fableCoreNpmDir "tsc" [sprintf "--project ../../%s/ts" fableCoreSrcDir]
    setEnvironVar "BABEL_ENV" "target-commonjs"
    Npm.script fableCoreNpmDir "babel" [". --out-dir . --compact=false"]
)

Target "UpdateSampleRequirements" (fun _ ->
    let fableVersion = "^" + releaseCompiler.Value.NugetVersion
    let fableCoreVersion = "^" + releaseCore.Value.NugetVersion

    !! "samples/**/package.json"
    |> Seq.iter (fun path ->
        (Path.GetDirectoryName path, ["fable"; "fable-core"])
        ||> Npm.updatePackageKeyValue (fun (k,v) ->
            match k with
            | "fable" when v <> fableVersion -> Some(k, fableVersion)
            | "fable-core" when v <> fableCoreVersion -> Some(k, fableCoreVersion)
            | _ -> None
    ))
)

Target "BrowseDocs" (fun _ ->
    let exit = Fake.executeFAKEWithOutput "docs" "docs.fsx" "" ["target", "BrowseDocs"]
    if exit <> 0 then failwith "Browsing documentation failed"
)

Target "GenerateDocs" (fun _ ->
    let exit = Fake.executeFAKEWithOutput "docs" "docs.fsx" "" ["target", "GenerateDocs"]
    if exit <> 0 then failwith "Generating documentation failed"
)

Target "PublishDocs" (fun _ ->
    let exit = Fake.executeFAKEWithOutput "docs" "docs.fsx" "" ["target", "PublishDocs"]
    if exit <> 0 then failwith "Publishing documentation failed"
)

Target "PublishStaticPages" (fun _ ->
    let exit = Fake.executeFAKEWithOutput "docs" "docs.fsx" "" ["target", "PublishStaticPages"]
    if exit <> 0 then failwith "Publishing documentation failed"
)

Target "All" ignore

// Build order
"Clean"
  ==> "FableCoreRelease"
  ==> "FableCompilerRelease"
  ==> "Plugins"
  ==> "MochaTest"
  =?> ("NUnitTest", environVar "APPVEYOR" = "True")
  =?> ("NUnitTest", environVar "TRAVIS" = "true")
  =?> ("MakeArtifactLighter", environVar "APPVEYOR" = "True")
  ==> "All"

"FableCoreRelease"
  ==> "PublishCore"

"Clean"
  ==> "FableCompilerNetcore"

"FableCompilerNetcore"
  ==> "PublishCompilerNetcore"

"Plugins"
  ==> "ES6MochaTest"

"FableCompilerDebug"
  ==> "QuickFableCompilerTest"

"FableCoreDebug"
  ==> "QuickFableCoreTest"

// Start build
RunTargetOrDefault "All"
