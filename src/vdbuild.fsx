namespace VDimensions.Fake

#load "./common.fsx"
#load "./paket.fsx"
open VDimensions.Fake.Common
open VDimensions.Fake.Paket

open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open Fake.Net

module VDBuild =

    let paketVersion = "5.207.3"

    let pwd = Shell.pwd()

    let runTestsOnBuild = false

    let createParams (data : list<string*string>) =
        let propertyFormat = " -p:{0}={1}"
        let sb = 
            data
            |> List.fold (fun (sb : System.Text.StringBuilder) (k, v) -> sb.AppendFormat(propertyFormat, k, v)) (System.Text.StringBuilder())
        sb.ToString()

    let getVersion propsFile =
        let yearSince = Xml.read true propsFile "" "" "Project/PropertyGroup/CopyrightYearSince" |> Seq.map System.Int32.Parse |> Seq.head
        let now = System.DateTime.UtcNow
        let backThen = System.DateTime(yearSince, 1, 1)
        let timeOfDay = now.TimeOfDay
        let major = Xml.read true propsFile "" "" "Project/PropertyGroup/VersionMajor" |> Seq.map System.Int32.Parse |> Seq.head
        let minor = Xml.read true propsFile "" "" "Project/PropertyGroup/VersionMinor" |> Seq.map System.Int32.Parse |> Seq.head
        let build = int((now - backThen).TotalDays)
        let revision = int(timeOfDay.TotalSeconds / 2.0)
        System.Version(major, minor, build, revision)


    let dir_exists = DirectoryInfo.ofPath >> DirectoryInfo.exists
    let rootDir = (Shell.pwd());
    let do_in_root<'a, 'b> (op : 'a -> 'b) (args : 'a) =
        let dir = (Shell.pwd());
        try
            Shell.cd rootDir
            op args
        finally
            Shell.cd dir
    let nupkg_map fn = 
        do_in_root (fun fn -> !!"../dist/*.nupkg" |> Seq.map fn |> List.ofSeq) fn

    let msbuild command arg =
        let msbuildExe = "msbuild"
        // run "msbuild.exe"
        Command.RawCommand(msbuildExe, Arguments.OfArgs [command; arg])
        |> runRetry 0
        |> ignore

    let clean () =
        //let objPath = Path.combine (Shell.pwd()) "obj"
        let paketFiles = Path.combine (Shell.pwd()) "pake-files"
        //Shell.rm_rf objPath
        Shell.rm_rf paketFiles
        //Shell.mkdir objPath

    let cleanNupkg = (fun _ -> nupkg_map (fun nupkg -> Trace.tracefn "NUPK CLEAN: %s" nupkg; Shell.rm_rf nupkg) |> ignore)

    let get_git_branch() = do_in_root (fun _ -> Fake.Tools.Git.Information.getBranchName(Shell.pwd())) ()

    open Fake.DotNet
    open Fake.DotNet.NuGet

    let dotnet_sdk = lazy DotNet.install DotNet.Versions.Release_2_1_4
    //let inline dotnet op = DotNet.exec (DotNet.Options.lift dotnet_sdk.Value) op
    let inline dotnet op cmd = DotNet.exec op cmd
    let inline dotnet_clean op arg = dotnet op "clean" arg
    let inline dotnet_restore op arg = dotnet op "restore" arg
    let inline dotnet_build op arg = dotnet op "build" arg
    let inline dotnet_pack op arg = dotnet op "pack" arg
    let inline dotnet_test op arg = dotnet op "test" arg
    let inline dotnet_nuget op arg = dotnet op "nuget" arg
    //    DotNet.exec (DotNet.Options.lift dotnet_sdk.Value) "restore" arg
    //let inline build arg =
    //    DotNet.exec dotnet "build" arg

    let build op args =
        let dir = Shell.pwd()
        match !!(sprintf "%s/*.fsproj" dir) |> List.ofSeq with
        | [] | [_] -> 
            Trace.tracefn "Performing dotnet build for default project in dir '%s'" dir
            dotnet_build op args |> ignore
        //| [singleFsProj] ->
        //    Trace.tracefn "Performing msbuild Build for %s in %s" singleFsProj dir
        //    // this is an F# project. dotnet build fails to produce valid metadata in the dll, we should use MSBuild instead (but not dotnet msbuild).
        //    msbuild "Build" singleFsProj
        | _ ->
            invalidOp "Multiple project files are not supported by this script. Please, make sure you have a single msbuild file in the directory of the given project."

    let createDynamicTarget propsFilePath location =
        let propsFile = (sprintf "%s/%s" pwd propsFilePath)
        let version = getVersion propsFile
        Trace.traceImportantfn "Project Version is '%s'" (version.ToString())
        
        let customDotnetdParams = 
            (fun () ->
                let b = version.Build
                let r = version.Revision
                let mutable p = [
                    ("VersionBuild", b.ToString())
                    ("VersionRevision", r.ToString())
                ] 
                createParams p
            )()

        let dotnet_options = 
            (fun (op : DotNet.Options) -> 
                { op with 
                    Verbosity = Some DotNet.Verbosity.Quiet
                    CustomParams = Some (customDotnetdParams)
                })

        let targetName = location
        Target.create targetName (fun _ ->
            let codeDir = sprintf "%s/src" location
            let testsDir = sprintf "%s/tests" location
            let mutable projectFileName = ""
            if (dir_exists codeDir) then
                Shell.pushd (codeDir)
                let dir = Shell.pwd()
                try
                    Trace.tracefn "Project to build %s" dir
                    projectFileName <- System.IO.Path.GetFileNameWithoutExtension((System.IO.DirectoryInfo(dir).GetFiles("*.??proj") |> Array.head).FullName)
                    clean ()
                    paket 3 paketVersion ["update"] |> ignore
                    dotnet_clean (dotnet_options) "" |> ignore
                    //dotnet_restore (dotnet_options) "" |> ignore
                    build (dotnet_options) ""
                    dotnet_pack (dotnet_options) "" |> ignore
                    Command.RawCommand("nuget", Arguments.OfArgs ["add"; sprintf "../../../../dist/%s.%s.nupkg" projectFileName (version.ToString()); "-Source"; "../../../../dist/restore"]) 
                    |> Common.runRetry 1
                    |> ignore
                    ()
                finally 
                    let paketdir = sprintf "%s/.paket" dir
                    Shell.rm_rf paketdir
                    Shell.popd()
            else ()

            if (dir_exists testsDir) then
                Shell.pushd (testsDir)
                let dir = Shell.pwd()
                try
                    Trace.tracefn "Test project to build %s" dir
                    clean ()
                    paket 3 paketVersion ["update"] |> ignore
                    dotnet_clean (dotnet_options) "" |> ignore
                    //dotnet_restore (dotnet_options) "" |> ignore
                    dotnet_build (dotnet_options) "" |> ignore
                    if runTestsOnBuild then dotnet_test (dotnet_options) "" |> ignore
                finally 
                    let paketdir = sprintf "%s/.paket" dir
                    Shell.rm_rf paketdir
                    Shell.popd()
            else 
                Trace.traceImportantfn "Project '%s' does not have tests" location
                ()
        )
        targetName