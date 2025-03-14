﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

module Microsoft.Quantum.EntryPointDriver.Tests

open System
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.VisualStudio.LanguageServer.Protocol
open Xunit

open Microsoft.Quantum.QsCompiler.CompilationBuilder
open Microsoft.Quantum.QsCompiler.CsharpGeneration
open Microsoft.Quantum.QsCompiler.ReservedKeywords
open Microsoft.Quantum.QsCompiler.SyntaxTree
open Microsoft.Quantum.Simulation.Simulators

/// The path to the Q# file that provides the Microsoft.Quantum.Core namespace.
let private coreFile = Path.GetFullPath "Core.qs"

/// The path to the Q# file that provides the Microsoft.Quantum.Intrinsic namespace.
let private intrinsicFile = Path.GetFullPath "Intrinsic.qs"

/// The path to the Q# file that contains the test cases.
let private testFile = Path.GetFullPath "Tests.qs"

/// The namespace used for the test cases.
let private testNamespace = "EntryPointTest"

/// <summary>
/// A map where each key is a test case name, and each value is the source code of the test case.
/// </summary>
/// <remarks>
/// Each test case corresponds to a section from <see cref="testFile"/>, separated by "// ---". The text immediately
/// after "// ---" until the end of the line is the name of the test case.
/// </remarks>
let private testCases =
    File.ReadAllText testFile
    |> fun text -> Environment.NewLine + "// ---" |> text.Split
    |> Seq.map (fun case ->
        let parts = case.Split (Environment.NewLine, 2)
        parts.[0].Trim (), parts.[1])
    |> Map.ofSeq

/// Compiles the Q# source code.
let private compileQSharp source =
    let uri name = Uri ("file://" + name)
    let fileManager name content =
        CompilationUnitManager.InitializeFileManager (uri name, content)

    let props = dict [ MSBuildProperties.ResolvedQsharpOutputType, AssemblyConstants.QsharpExe ] |> ProjectProperties
    use compilationManager = new CompilationUnitManager (props)
    let fileManagers = ImmutableHashSet.Create (fileManager coreFile (File.ReadAllText coreFile),
                                                fileManager intrinsicFile (File.ReadAllText intrinsicFile),
                                                fileManager testFile source)
    compilationManager.AddOrUpdateSourceFilesAsync fileManagers |> ignore
    let compilation = compilationManager.Build ()
    let errors =
        compilation.Diagnostics ()
        |> Seq.filter (fun diagnostic -> diagnostic.Severity = Nullable DiagnosticSeverity.Error)
    Assert.Empty errors
    compilation.BuiltCompilation

/// Generates C# source code from the compiled Q# syntax tree using the given assembly constants.
let private generateCSharp constants (compilation : QsCompilation) =
    let context = CodegenContext.Create (compilation, constants)
    let entryPoints = seq { for ep in compilation.EntryPoints -> context.allCallables.[ep] }
    [
        SimulationCode.generate testFile context
        EntryPoint.generateSource context entryPoints
        EntryPoint.generateMainSource context entryPoints
    ]

/// The full path to a referenced assembly given its short name.
let private referencedAssembly name =
    let delimiter = if Environment.OSVersion.Platform = PlatformID.Win32NT then ';' else ':'
    let path =
        (AppContext.GetData "TRUSTED_PLATFORM_ASSEMBLIES" :?> string).Split delimiter
        |> Seq.tryFind (fun path -> String.Equals (Path.GetFileNameWithoutExtension path, name,
                                                   StringComparison.InvariantCultureIgnoreCase))
    path |> Option.defaultWith (fun () -> failwith (sprintf "Missing reference to assembly '%s'." name))

/// Compiles the C# sources into an assembly.
let private compileCSharp (sources : string seq) =
    let references : MetadataReference list =
        [
            "netstandard"
            "System.Collections.Immutable"
            "System.CommandLine"
            "System.Console"
            "System.Linq"
            "System.Private.CoreLib"
            "System.Runtime"
            "System.Runtime.Extensions"
            "System.Runtime.Numerics"
            "Microsoft.Quantum.EntryPointDriver"
            "Microsoft.Quantum.QSharp.Foundation"
            "Microsoft.Quantum.QSharp.Core"
            "Microsoft.Quantum.QsDataStructures"
            "Microsoft.Quantum.Runtime.Core"
            "Microsoft.Quantum.Simulation.Common"
            "Microsoft.Quantum.Simulators"
            "Microsoft.Quantum.Targets.Interfaces"
        ]
        |> List.map (fun name -> upcast MetadataReference.CreateFromFile (referencedAssembly name))

    let syntaxTrees = sources |> Seq.map CSharpSyntaxTree.ParseText
    let compilation = CSharpCompilation.Create ("GeneratedEntryPoint", syntaxTrees, references)
    use stream = new MemoryStream ()
    let result = compilation.Emit stream
    Assert.True (result.Success, String.Join ("\n", result.Diagnostics))
    Assert.Equal (0L, stream.Seek (0L, SeekOrigin.Begin))
    Assembly.Load (stream.ToArray ())

/// The assembly for the given test case assembly constants.
let private testAssembly testName constants =
    testCases
    |> Map.find testName
    |> compileQSharp
    |> generateCSharp constants
    |> compileCSharp

/// Runs the entry point in the assembly with the given command-line arguments, and returns the output, errors, and exit
/// code.
let private run (assembly : Assembly) (args : string[]) =
    let entryPoint = assembly.GetType (sprintf "%s.%s" EntryPoint.entryPointClassName EntryPoint.entryPointClassName)
    let main = entryPoint.GetMethod("Main", BindingFlags.NonPublic ||| BindingFlags.Static)
    let previousCulture = CultureInfo.DefaultThreadCurrentCulture
    let previousOut = Console.Out
    let previousError = Console.Error

    CultureInfo.DefaultThreadCurrentCulture <- CultureInfo ("en-US", false)
    use outStream = new StringWriter ()
    use errorStream = new StringWriter ()
    try
        Console.SetOut outStream
        Console.SetError errorStream
        let exitCode = main.Invoke (null, [| args |]) :?> Task<int> |> Async.AwaitTask |> Async.RunSynchronously
        outStream.ToString (), errorStream.ToString (), exitCode
    finally
        Console.SetError previousError
        Console.SetOut previousOut
        CultureInfo.DefaultThreadCurrentCulture <- previousCulture

/// Replaces every sequence of whitespace characters in the string with a single space.
let private normalize s = Regex.Replace(s, @"\s+", " ").Trim()

/// Asserts that running the entry point in the assembly with the given arguments succeeds and yields the expected
/// output. The standard error and out streams of the actual output are concatenated in that order.
let private yields expected (assembly, args) =
    let out, error, exitCode = run assembly args
    Assert.True (0 = exitCode, sprintf "Expected exit code 0, but got %d with:\n\n%s\n\n%s" exitCode error out)
    Assert.Equal (normalize expected, normalize (error + out))

/// Asserts that running the entry point in the assembly with the given arguments fails.
let private fails (assembly, args) =
    let out, error, exitCode = run assembly args
    Assert.True (0 <> exitCode, sprintf "Expected non-zero exit code, but got 0 with:\n\n%s\n\n%s" error out)

/// Asserts that running the entry point in the assembly with the given arguments fails and the output starts with the
/// expected message. The standard error and out streams of the actual output are concatenated in that order.
let private failsWith expected (assembly, args) =
    let out, error, exitCode = run assembly args
    Assert.True (0 <> exitCode, sprintf "Expected non-zero exit code, but got 0 with:\n\n%s\n\n%s" error out)
    Assert.StartsWith (normalize expected, normalize (error + out))

/// A tuple of the test assembly and arguments using the given assembly constants. The tuple can be passed to yields or
/// fails.
let private testWithConstants constants testNum =
    let assembly = testAssembly testNum constants
    fun args -> assembly, Array.ofList args

/// A tuple of the test assembly and arguments with no assembly constants. The tuple can be passed to yields or fails.
let private test = testWithConstants ImmutableDictionary.Empty

/// A tuple of the test assembly and arguments using the given default simulator. The tuple can be passed to yields or
/// fails.
let private testWithSim defaultSimulator =
    ImmutableDictionary.CreateRange [KeyValuePair (AssemblyConstants.DefaultSimulator, defaultSimulator)]
    |> testWithConstants

/// A tuple of the test assembly and arguments using the given default target. The tuple can be passed to yields or
/// fails.
let private testWithTarget defaultTarget =
    ImmutableDictionary.CreateRange [KeyValuePair (AssemblyConstants.ExecutionTarget, defaultTarget)]
    |> testWithConstants

/// Standard command-line arguments for the "submit" command without specifying a target.
let private submitWithoutTargetAndLocation =
    [ "submit"
      "--subscription"
      "mySubscription"
      "--resource-group"
      "myResourceGroup"
      "--workspace"
      "myWorkspace" ]

      
/// Standard command-line arguments for the "submit" command without specifying a target.
let private submitWithoutTarget = submitWithoutTargetAndLocation @ ["--location"; "myLocation"]

/// Standard command-line arguments for the "submit" command using the "test.machine.noop" target.
let private submitWithNoOpTarget = submitWithoutTarget @ ["--target"; "test.machine.noop"]

/// Standard command-line arguments for the "submit" command using the "test.machine.error" target.
let private submitWithErrorTarget = submitWithoutTarget @ ["--target"; "test.machine.error"]

// No Option

[<Fact>]
let ``Returns Unit`` () =
    let given = test "Returns Unit"
    given [] |> yields ""

[<Fact>]
let ``Returns Int`` () =
    let given = test "Returns Int"
    given [] |> yields "42"

[<Fact>]
let ``Returns String`` () =
    let given = test "Returns String"
    given [] |> yields "Hello, World!"

[<Fact>]
let ``Namespace and callable use same name`` () =
    let given = test "Namespace and callable use same name"
    given [] |> yields ""

// Single Option

[<Fact>]
let ``Accepts Int`` () =
    let given = test "Accepts Int"
    given ["-n"; "42"] |> yields "42"
    given ["-n"; "4.2"] |> fails
    given ["-n"; "9223372036854775807"] |> yields "9223372036854775807"
    given ["-n"; "9223372036854775808"] |> fails
    given ["-n"; "foo"] |> fails

[<Fact>]
let ``Accepts BigInt`` () =
    let given = test "Accepts BigInt"
    given ["-n"; "42"] |> yields "42"
    given ["-n"; "4.2"] |> fails
    given ["-n"; "9223372036854775807"] |> yields "9223372036854775807"
    given ["-n"; "9223372036854775808"] |> yields "9223372036854775808"
    given ["-n"; "foo"] |> fails

[<Fact>]
let ``Accepts Double`` () =
    let given = test "Accepts Double"
    given ["-n"; "4.2"] |> yields "4.2"
    given ["-n"; "foo"] |> fails

[<Fact>]
let ``Accepts Bool`` () =
    let given = test "Accepts Bool"
    given ["-b"; "false"] |> yields "False"
    given ["-b"; "true"] |> yields "True"
    given ["-b"; "one"] |> fails
    given ["-b"] |> fails

[<Fact>]
let ``Accepts Pauli`` () =
    let given = test "Accepts Pauli"
    given ["-p"; "PauliI"] |> yields "PauliI"
    given ["-p"; "PauliX"] |> yields "PauliX"
    given ["-p"; "PauliY"] |> yields "PauliY"
    given ["-p"; "PauliZ"] |> yields "PauliZ"
    given ["-p"; "paulii"] |> yields "PauliI"
    given ["-p"; "paulix"] |> yields "PauliX"
    given ["-p"; "pauliy"] |> yields "PauliY"
    given ["-p"; "pauliz"] |> yields "PauliZ"
    given ["-p"; "PauliW"] |> fails

[<Fact>]
let ``Accepts Result`` () =
    let given = test "Accepts Result"
    given ["-r"; "Zero"] |> yields "Zero"
    given ["-r"; "zero"] |> yields "Zero"
    given ["-r"; "One"] |> yields "One"
    given ["-r"; "one"] |> yields "One"
    given ["-r"; "0"] |> yields "Zero"
    given ["-r"; "1"] |> yields "One"
    given ["-r"; "Two"] |> fails

[<Fact>]
let ``Accepts Range`` () =
    let given = test "Accepts Range"
    given ["-r"; "0..0"] |> yields "0..1..0"
    given ["-r"; "0..1"] |> yields "0..1..1"
    given ["-r"; "0..2..10"] |> yields "0..2..10"
    given ["-r"; "0 ..1"] |> yields "0..1..1"
    given ["-r"; "0.. 1"] |> yields "0..1..1"
    given ["-r"; "0 .. 1"] |> yields "0..1..1"
    given ["-r"; "0 ..2 ..10"] |> yields "0..2..10"
    given ["-r"; "0.. 2 ..10"] |> yields "0..2..10"
    given ["-r"; "0 .. 2 .. 10"] |> yields "0..2..10"
    given ["-r"; "0 1"] |> fails
    given ["-r"; "0 2 10"] |> fails
    given ["-r"; "0"; "1"] |> fails
    given ["-r"; "0"] |> fails
    given ["-r"; "0.."] |> fails
    given ["-r"; "0..2.."] |> fails
    given ["-r"; "0..2..3.."] |> fails
    given ["-r"; "0..2..3..4"] |> fails
    given ["-r"; "0"; ".."; "1"] |> fails
    given ["-r"; "0..1"; "..2"] |> fails

[<Fact>]
let ``Accepts String`` () =
    let given = test "Accepts String"
    given ["-s"; "Hello, World!"] |> yields "Hello, World!"

[<Fact>]
let ``Accepts Unit`` () =
    let given = test "Accepts Unit"
    given ["-u"; "()"] |> yields ""
    given ["-u"; "42"] |> fails

[<Fact>]
let ``Accepts String array`` () =
    let given = test "Accepts String array"
    given ["--xs"; "foo"] |> yields "[foo]"
    given ["--xs"; "foo"; "bar"] |> yields "[foo,bar]"
    given ["--xs"; "foo bar"; "baz"] |> yields "[foo bar,baz]"
    given ["--xs"; "foo"; "bar"; "baz"] |> yields "[foo,bar,baz]"
    given ["--xs"] |> fails

[<Fact>]
let ``Accepts BigInt array`` () =
    let given = test "Accepts BigInt array"
    given ["--bs"; "9223372036854775808"] |> yields "[9223372036854775808]"
    given ["--bs"; "1"; "2"; "9223372036854775808"] |> yields "[1,2,9223372036854775808]"
    given ["--bs"; "1"; "2"; "4.2"] |> fails

[<Fact>]
let ``Accepts Pauli array`` () =
    let given = test "Accepts Pauli array"
    given ["--ps"; "PauliI"] |> yields "[PauliI]"
    given ["--ps"; "PauliZ"; "PauliX"] |> yields "[PauliZ,PauliX]"
    given ["--ps"; "PauliY"; "PauliY"; "PauliY"] |> yields "[PauliY,PauliY,PauliY]"
    given ["--ps"; "PauliY"; "PauliZ"; "PauliW"] |> fails

[<Fact>]
let ``Accepts Range array`` () =
    let given = test "Accepts Range array"
    given ["--rs"; "1..10"] |> yields "[1..1..10]"
    given ["--rs"; "1..10"; "2..4..20"] |> yields "[1..1..10,2..4..20]"
    given ["--rs"; "1 ..10"; "2 ..4 ..20"] |> yields "[1..1..10,2..4..20]"
    given ["--rs"; "1 .. 10"; "2 .. 4 .. 20"] |> yields "[1..1..10,2..4..20]"
    given ["--rs"; "1 .. 1o"; "2 .. 4 .. 20"] |> fails

[<Fact>]
let ``Accepts Result array`` () =
    let given = test "Accepts Result array"
    given ["--rs"; "One"] |> yields "[One]"
    given ["--rs"; "One"; "Zero"] |> yields "[One,Zero]"
    given ["--rs"; "Zero"; "One"; "Two"] |> fails

[<Fact>]
let ``Accepts Unit array`` () =
    let given = test "Accepts Unit array"
    given ["--us"; "()"] |> yields "[()]"
    given ["--us"; "()"; "()"] |> yields "[(),()]"
    given ["--us"; "()"; "()"; "()"] |> yields "[(),(),()]"
    given ["--us"; "()"; "unit"; "()"] |> fails

[<Fact>]
let ``Supports repeat-name array syntax`` () =
    let given = test "Accepts String array"
    given ["--xs"; "Hello"; "--xs"; "World"] |> yields "[Hello,World]"
    given ["--xs"; "foo"; "bar"; "--xs"; "baz"] |> yields "[foo,bar,baz]"
    given ["--xs"; "foo"; "--xs"; "bar"; "--xs"; "baz"] |> yields "[foo,bar,baz]"
    given ["--xs"; "foo bar"; "--xs"; "baz"] |> yields "[foo bar,baz]"

// Multiple Options

[<Fact>]
let ``Accepts two options`` () =
    let given = test "Accepts two options"
    given ["-n"; "7"; "-b"; "true"] |> yields "7 True"
    given ["-b"; "true"; "-n"; "7"] |> yields "7 True"

[<Fact>]
let ``Accepts three options`` () =
    let given = test "Accepts three options"
    given ["-n"; "7"; "-b"; "true"; "--xs"; "foo"] |> yields "7 True [foo]"
    given ["--xs"; "foo"; "-n"; "7"; "-b"; "true"] |> yields "7 True [foo]"
    given ["-n"; "7"; "--xs"; "foo"; "-b"; "true"] |> yields "7 True [foo]"
    given ["-b"; "true"; "-n"; "7"; "--xs"; "foo"] |> yields "7 True [foo]"

[<Fact>]
let ``Requires all options`` () =
    let given = test "Accepts three options"
    given ["-b"; "true"; "--xs"; "foo"] |> fails
    given ["-n"; "7"; "--xs"; "foo"] |> fails
    given ["-n"; "7"; "-b"; "true"] |> fails
    given ["-n"; "7"] |> fails
    given ["-b"; "true"] |> fails
    given ["--xs"; "foo"] |> fails
    given [] |> fails

// Tuples

[<Fact>]
let ``Accepts redundant one-tuple`` () =
    let given = test "Accepts redundant one-tuple"
    given ["-x"; "7"] |> yields "7"

[<Fact>]
let ``Accepts redundant two-tuple`` () =
    let given = test "Accepts redundant two-tuple"
    given ["-x"; "7"; "-y"; "8"] |> yields "7 8"

[<Fact>]
let ``Accepts one-tuple`` () =
    let given = test "Accepts one-tuple"
    given ["-x"; "7"; "-y"; "8"] |> yields "7 8"

[<Fact>]
let ``Accepts two-tuple`` () =
    let given = test "Accepts two-tuple"
    given ["-x"; "7"; "-y"; "8"; "-z"; "9"] |> yields "7 8 9"

// Name Conversion

[<Fact>]
let ``Uses kebab-case`` () =
    let given = test "Uses kebab-case"
    given ["--camel-case-name"; "foo"] |> yields "foo"
    given ["--camelCaseName"; "foo"] |> fails

[<Fact>]
let ``Uses single-dash short names`` () =
    let given = test "Uses single-dash short names"
    given ["-x"; "foo"] |> yields "foo"
    given ["--x"; "foo"] |> fails

// Shadowing

[<Fact>]
let ``Shadows --simulator`` () =
    let given = test "Shadows --simulator"
    given ["--simulator"; "foo"]
    |> yields "Warning: Option --simulator is overridden by an entry point parameter name. Using default value QuantumSimulator.
               foo"
    given ["--simulator"; AssemblyConstants.ToffoliSimulator]
    |> yields (sprintf "Warning: Option --simulator is overridden by an entry point parameter name. Using default value QuantumSimulator.
                        %s"
                       AssemblyConstants.ToffoliSimulator)
    given ["-s"; AssemblyConstants.ToffoliSimulator; "--simulator"; "foo"] |> fails
    given ["-s"; "foo"] |> fails

[<Fact>]
let ``Shadows -s`` () =
    let given = test "Shadows -s"
    given ["-s"; "foo"] |> yields "foo"
    given ["--simulator"; AssemblyConstants.ToffoliSimulator; "-s"; "foo"] |> yields "foo"
    given ["--simulator"; "bar"; "-s"; "foo"] |> fails

[<Fact>]
let ``Shadows --version`` () =
    let given = test "Shadows --version"
    given ["--version"; "foo"] |> yields "foo"

[<Fact>]
let ``Shadows --target`` () =
    let given = test "Shadows --target"
    given ["--target"; "foo"] |> yields "foo"
    given submitWithNoOpTarget
    |> failsWith "The required option --target conflicts with an entry point parameter name."

[<Fact>]
let ``Shadows --shots`` () =
    let given = test "Shadows --shots"
    given ["--shots"; "7"] |> yields "7"
    given (submitWithNoOpTarget @ ["--shots"; "7"])
    |> yields "Warning: Option --shots is overridden by an entry point parameter name. Using default value 500.
               https://www.example.com/00000000-0000-0000-0000-0000000000000"

// Simulators

[<Fact>]
let ``Supports QuantumSimulator`` () =
    let given = test "X or H"
    given ["--simulator"; AssemblyConstants.QuantumSimulator; "--use-h"; "false"] |> yields "Hello, World!"
    given ["--simulator"; AssemblyConstants.QuantumSimulator; "--use-h"; "true"] |> yields "Hello, World!"

[<Fact>]
let ``Supports ToffoliSimulator`` () =
    let given = test "X or H"
    given ["--simulator"; AssemblyConstants.ToffoliSimulator; "--use-h"; "false"] |> yields "Hello, World!"
    given ["--simulator"; AssemblyConstants.ToffoliSimulator; "--use-h"; "true"] |> fails

[<Fact>]
let ``Rejects unknown simulator`` () =
    let given = test "X or H"
    given ["--simulator"; "FooSimulator"; "--use-h"; "false"] |> fails

[<Fact>]
let ``Supports default custom simulator`` () =
    // This is not really a "custom" simulator, but the driver does not recognize the fully-qualified name of the
    // standard simulators, so it is treated as one.
    let given = testWithSim typeof<ToffoliSimulator>.FullName "X or H"
    given ["--use-h"; "false"] |> yields "Hello, World!"
    given ["--use-h"; "true"] |> fails
    given ["--simulator"; typeof<ToffoliSimulator>.FullName; "--use-h"; "false"] |> yields "Hello, World!"
    given ["--simulator"; typeof<ToffoliSimulator>.FullName; "--use-h"; "true"] |> fails
    given ["--simulator"; AssemblyConstants.QuantumSimulator; "--use-h"; "false"] |> yields "Hello, World!"
    given ["--simulator"; AssemblyConstants.QuantumSimulator; "--use-h"; "true"] |> yields "Hello, World!"
    given ["--simulator"; typeof<QuantumSimulator>.FullName; "--use-h"; "false"] |> fails

// Azure Quantum Submission

[<Fact>]
let ``Submit can submit a job`` () =
    let given = test "Returns Unit"
    given submitWithNoOpTarget
    |> yields "https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit can show only the ID`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ ["--output"; "id"]) |> yields "00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit uses default values`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ ["--verbose"])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage:
               Base URI:
               Location: myLocation
               Credential: Default
               AadToken:
               UserAgent:
               Job Name:
               Job Parameters:
               Shots: 500
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit uses default values with default target`` () =
    let given = testWithTarget "test.machine.noop" "Returns Unit"
    given (submitWithoutTarget @ ["--verbose"])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage:
               Base URI:
               Location: myLocation
               Credential: Default
               AadToken:
               UserAgent:
               Job Name:
               Job Parameters:
               Shots: 500
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit allows overriding default values`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ [
        "--verbose"
        "--storage"
        "myStorage"
        "--aad-token"
        "myToken"
        "--job-name"
        "myJobName"
        "--shots"
        "750"
        "--user-agent"
        "myUserAgent"
        "--credential"
        "cli"
    ])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage: myStorage
               Base URI:
               Location: myLocation
               Credential: CLI
               AadToken: myTok
               UserAgent: myUserAgent
               Job Name: myJobName
               Job Parameters:
               Shots: 750
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"


[<Fact>]
let ``Submit allows a long user-agent`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ [
        "--verbose"
        "--storage"
        "myStorage"
        "--aad-token"
        "myToken"
        "--job-name"
        "myJobName"
        "--shots"
        "750"
        "--user-agent"
        "a-very-long-user-agent-(it-will-be-not-be-truncated-here)"
        "--credential"
        "cli"
    ])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage: myStorage
               Base URI:
               Location: myLocation
               Credential: CLI
               AadToken: myTok
               UserAgent: a-very-long-user-agent-(it-will-be-not-be-truncated-here)
               Job Name: myJobName
               Job Parameters:
               Shots: 750
               Output: FriendlyUri
               Dry Run: False
               Verbose: True
               Submitting Q# entry point using a quantum machine.
               https://www.example.com/00000000-0000-0000-0000-0000000000000"


[<Fact>]
let ``Submit extracts the location from a quantum endpoint`` () =
    let given = test "Returns Unit"
    given (submitWithoutTargetAndLocation @ [
        "--verbose"
        "--storage"
        "myStorage"
        "--aad-token"
        "myToken"
        "--base-uri"
        "https://westus.quantum.microsoft.com/"
        "--job-name"
        "myJobName"
        "--credential"
        "VisualStudio"
        "--user-agent"
        "myUserAgent"
        "--shots"
        "750"
        "--target"
        "test.machine.noop"
    ])
    |> yields "Subscription: mySubscription
                Resource Group: myResourceGroup
                Workspace: myWorkspace
                Target: test.machine.noop
                TargetCapability:
                Storage: myStorage
                Base URI: https://westus.quantum.microsoft.com/
                Location: westus
                Credential: VisualStudio
                AadToken: myTok
                UserAgent: myUserAgent
                Job Name: myJobName
                Job Parameters:
                Shots: 750
                Output: FriendlyUri
                Dry Run: False
                Verbose: True

                Submitting Q# entry point using a quantum machine.
               
                https://www.example.com/00000000-0000-0000-0000-0000000000000"
               
[<Fact>]
let ``Submit allows overriding default values with default target`` () =
    let given = testWithTarget "foo.target" "Returns Unit"
    given (submitWithNoOpTarget @ [
        "--verbose"
        "--storage"
        "myStorage"
        "--credential"
        "Interactive"
        "--aad-token"
        "myToken"
        "--job-name"
        "myJobName"
        "--shots"
        "750"
    ])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage: myStorage
               Base URI:
               Location: myLocation
               Credential: Interactive
               AadToken: myTok
               UserAgent:
               Job Name: myJobName
               Job Parameters:
               Shots: 750
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit does not allow to include mutually exclusive options`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ [
        "--base-uri"
        "myBaseUri"
    ])
    |> failsWith "Options --base-uri, --location cannot be used together."
    
[<Fact>]
let ``Submit allows to include --base-uri option when --location is not present`` () =
    let given = testWithTarget "foo.target" "Returns Unit"
    given (submitWithoutTargetAndLocation @ [
        "--verbose"
        "--base-uri"
        "http://myBaseUri.foo.com/"
        "--target"
        "test.machine.noop"
    ])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage:
               Base URI: http://mybaseuri.foo.com/
               Location: mybaseuri
               Credential: Default
               AadToken:
               UserAgent:
               Job Name:
               Job Parameters:
               Shots: 500
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit allows to include --location option when --base-uri is not present`` () =
    let given = testWithTarget "foo.target" "Returns Unit"
    given (submitWithNoOpTarget @ [
        "--verbose"
    ])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage:
               Base URI:
               Location: myLocation
               Credential: Default
               AadToken:
               UserAgent:
               Job Name:
               Job Parameters:
               Shots: 500
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit allows spaces for the --location option`` () =
    let given = test "Returns Unit"
    given (submitWithoutTargetAndLocation @ [
        "--verbose"
        "--location"
        "My Location"
        "--target" 
        "test.machine.noop"
    ])
    |> yields "Subscription: mySubscription
               Resource Group: myResourceGroup
               Workspace: myWorkspace
               Target: test.machine.noop
               TargetCapability:
               Storage:
               Base URI:
               Location: My Location
               Credential: Default
               AadToken:
               UserAgent:
               Job Name:
               Job Parameters:
               Shots: 500
               Output: FriendlyUri
               Dry Run: False
               Verbose: True

               Submitting Q# entry point using a quantum machine.

               https://www.example.com/00000000-0000-0000-0000-0000000000000"
    
[<Fact>]
let ``Submit does not allow an invalid value for the --location option`` () =
    let given = test "Returns Unit"
    given (submitWithoutTargetAndLocation @ [
        "--target" 
        "test.machine.noop"
        "--location"
        "my!nv@lidLocation"
    ])
    |> failsWith "\"my!nv@lidLocation\" is an invalid value for the --location option."

[<Fact>]
let ``Submit fails if both --location and --baseUri are missing`` () =
    let given = test "Returns Unit"
    given (submitWithoutTargetAndLocation @ [
        "--target" 
        "test.machine.noop"
    ])
    |> failsWith "Either --location or --base-uri must be provided."

[<Fact>]
let ``Submit requires a positive number of shots`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ ["--shots"; "1"])
    |> yields "https://www.example.com/00000000-0000-0000-0000-0000000000000"
    given (submitWithNoOpTarget @ ["--shots"; "0"]) |> fails
    given (submitWithNoOpTarget @ ["--shots"; "-1"]) |> fails

[<Fact>]
let ``Submit fails with unknown target`` () =
    let given = test "Returns Unit"
    given (submitWithoutTarget @ ["--target"; "foo"]) |> failsWith "No submitters were found for the target \"foo\" and target capability \"\"."

[<Fact>]
let ``Submit supports dry run option`` () =
    let given = test "Returns Unit"
    given (submitWithNoOpTarget @ ["--dry-run"]) |> yields "✔️  The program is valid!"
    given (submitWithErrorTarget @ ["--dry-run"])
    |> failsWith "❌  The program is invalid.

                  This quantum machine always has an error."

[<Fact>]
let ``Submit has required options`` () =
    // Returns the "power set" of a list: every possible combination of elements in the list without changing the order.
    let rec powerSet = function
        | [] -> [[]]
        | x :: xs ->
            let next = powerSet xs
            List.map (fun list -> x :: list) next @ next
    let given = test "Returns Unit"

    // Try every possible combination of arguments. The command should succeed only when all of the arguments are
    // included.
    let commandName = List.head submitWithNoOpTarget
    let allArgs = submitWithNoOpTarget |> List.tail |> List.chunkBySize 2
    for args in powerSet allArgs do
        given (commandName :: List.concat args)
        |> if List.length args = List.length allArgs
           then yields "https://www.example.com/00000000-0000-0000-0000-0000000000000"
           else fails

[<Fact>]
let ``Submit catches exceptions`` () =
    let given = test "Returns Unit"
    given submitWithErrorTarget
    |> failsWith "An error occurred when submitting to the Azure Quantum service.

                  This machine always has an error."

[<Fact>]
let ``Submit shows specific error message when QIR stream is unavailable`` () =
    let given = testWithTarget "test.submitter.qir.noop" "Returns Unit"

    given submitWithoutTarget
    |> failsWith
        ("The target test.submitter.qir.noop requires QIR submission, but the project was built without QIR. "
         + "Please enable QIR generation in the project settings.")

[<Fact>]
let ``Submit supports Q# submitters`` () =
    let given = testWithTarget "test.submitter.noop" "Returns Unit"

    given (submitWithoutTarget @ ["--verbose"])
    |> yields
        "Subscription: mySubscription
         Resource Group: myResourceGroup
         Workspace: myWorkspace
         Target: test.submitter.noop
         TargetCapability:
         Storage:
         Base URI:
         Location: myLocation
         Credential: Default
         AadToken:
         UserAgent:
         Job Name:
         Job Parameters:
         Shots: 500
         Output: FriendlyUri
         Dry Run: False
         Verbose: True

         Submitting Q# entry point.

         https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit supports job parameters`` () =
    let given = testWithTarget "test.submitter.noop" "Returns Unit"

    given (submitWithoutTarget @ ["--job-params"; "foo=bar"; "baz=qux"; "--verbose"])
    |> yields
        "Subscription: mySubscription
         Resource Group: myResourceGroup
         Workspace: myWorkspace
         Target: test.submitter.noop
         TargetCapability:
         Storage:
         Base URI:
         Location: myLocation
         Credential: Default
         AadToken:
         UserAgent:
         Job Name:
         Job Parameters: [baz, qux], [foo, bar]
         Shots: 500
         Output: FriendlyUri
         Dry Run: False
         Verbose: True

         Submitting Q# entry point.

         https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Extra equals symbols in a job parameter are parsed as part of the value`` () =
    let given = testWithTarget "test.submitter.noop" "Returns Unit"

    given (submitWithoutTarget @ ["--job-params"; "foo=bar=baz"; "--verbose"])
    |> yields
        "Subscription: mySubscription
         Resource Group: myResourceGroup
         Workspace: myWorkspace
         Target: test.submitter.noop
         TargetCapability:
         Storage:
         Base URI:
         Location: myLocation
         Credential: Default
         AadToken:
         UserAgent:
         Job Name:
         Job Parameters: [foo, bar=baz]
         Shots: 500
         Output: FriendlyUri
         Dry Run: False
         Verbose: True

         Submitting Q# entry point.

         https://www.example.com/00000000-0000-0000-0000-0000000000000"

[<Fact>]
let ``Submit fails if job parameters can't be parsed`` () =
    let given = testWithTarget "test.submitter.noop" "Returns Unit"

    given (submitWithoutTarget @ ["--job-params"; "foobar"])
    |> failsWith "This is not a \"key=value\" pair: 'foobar'"

// Help

[<Fact>]
let ``Uses documentation`` () =
    let name = Path.GetFileNameWithoutExtension (Assembly.GetEntryAssembly().Location)
    let message =
        (name, name)
        ||> sprintf "%s:
                     This test checks that the entry point documentation appears correctly in the command line help message.

                     Usage:
                       %s [options] [command]

                     Options:
                       -n <n> (REQUIRED)                                   An integer.
                       --pauli <PauliI|PauliX|PauliY|PauliZ> (REQUIRED)    The name of a Pauli matrix.
                       --my-cool-bool (REQUIRED)                           A neat bit.
                       -s, --simulator <simulator>                         The name of the simulator to use.
                       --version                                           Show version information
                       -?, -h, --help                                      Show help and usage information

                     Commands:
                       simulate    (default) Run the program using a local simulator."

    let given = test "Help"
    given ["--help"] |> yields message
    given ["-h"] |> yields message
    given ["-?"] |> yields message

[<Fact>]
let ``Shows help text for generateazurepayload command`` () =
    let name = Path.GetFileNameWithoutExtension (Assembly.GetEntryAssembly().Location)
    let message =
        name
        |> sprintf "Usage:
                      %s generateazurepayload [options]

                    Options:
                      -n <n> (REQUIRED)                                   An integer.
                      --pauli <PauliI|PauliX|PauliY|PauliZ> (REQUIRED)    The name of a Pauli matrix.
                      --my-cool-bool (REQUIRED)                           A neat bit.
                      --target <target> (REQUIRED)                        The target device ID.
                      --verbose                                           Show additional information about the submission.
                      -?, -h, --help                                      Show help and usage information"
    let given = test "Help"
    given ["generateazurepayload"; "--help"] |> yields message

[<Fact>]
let ``Shows help text for generateazurepayload command with default target`` () =
    let name = Path.GetFileNameWithoutExtension (Assembly.GetEntryAssembly().Location)
    let message =
        name
        |> sprintf "Usage:
                      %s generateazurepayload [options]

                    Options:
                      -n <n> (REQUIRED)                                   An integer.
                      --pauli <PauliI|PauliX|PauliY|PauliZ> (REQUIRED)    The name of a Pauli matrix.
                      --my-cool-bool (REQUIRED)                           A neat bit.
                      --target <target>                                   The target device ID.
                      --verbose                                           Show additional information about the submission.
                      -?, -h, --help                                      Show help and usage information"
    let given = testWithTarget "foo.target" "Help"
    given ["generateazurepayload"; "--help"] |> yields message

[<Fact>]
let ``Shows help text for submit command`` () =
    let name = Path.GetFileNameWithoutExtension (Assembly.GetEntryAssembly().Location)
    let message =
        name
        |> sprintf "Usage:
                      %s submit [options]

                    Options:
                      -n <n> (REQUIRED)                                   An integer.
                      --pauli <PauliI|PauliX|PauliY|PauliZ> (REQUIRED)    The name of a Pauli matrix.
                      --my-cool-bool (REQUIRED)                           A neat bit.
                      --subscription <subscription> (REQUIRED)            The subscription ID.
                      --resource-group <resource-group> (REQUIRED)        The resource group name.
                      --workspace <workspace> (REQUIRED)                  The workspace name.
                      --target <target> (REQUIRED)                        The target device ID.
                      --credential <credential>                           The type of credential to use to authenticate with Azure.
                      --storage <storage>                                 The storage account connection string.
                      --aad-token <aad-token>                             The Azure Active Directory authentication token.
                      --user-agent <user-agent>                           A label to identify this application when making requests to Azure Quantum.
                      --base-uri <base-uri>                               The base URI of the Azure Quantum endpoint.
                      --location <location>                               The location to use with the default endpoint.
                      --job-name <job-name>                               The name of the submitted job.
                      --job-params <job-params>                           Additional target-specific parameters for the submitted job (in the format \"key=value\").
                      --shots <shots>                                     The number of times the program is executed on the target machine.
                      --output <FriendlyUri|Id>                           The information to show in the output after the job is submitted.
                      --dry-run                                           Validate the program and options, but do not submit to Azure Quantum.
                      --verbose                                           Show additional information about the submission.
                      --submit-config-file <submit-config-file>           Configuration file to use custom submit properties.
                      -?, -h, --help                                      Show help and usage information"
    let given = test "Help"
    given ["submit"; "--help"] |> yields message

[<Fact>]
let ``Shows help text for submit command with default target`` () =
    let name = Path.GetFileNameWithoutExtension (Assembly.GetEntryAssembly().Location)
    let message =
        name
        |> sprintf "Usage:
                      %s submit [options]

                    Options:
                      -n <n> (REQUIRED)                                   An integer.
                      --pauli <PauliI|PauliX|PauliY|PauliZ> (REQUIRED)    The name of a Pauli matrix.
                      --my-cool-bool (REQUIRED)                           A neat bit.
                      --subscription <subscription> (REQUIRED)            The subscription ID.
                      --resource-group <resource-group> (REQUIRED)        The resource group name.
                      --workspace <workspace> (REQUIRED)                  The workspace name.
                      --target <target>                                   The target device ID.
                      --credential <credential>                           The type of credential to use to authenticate with Azure.
                      --storage <storage>                                 The storage account connection string.
                      --aad-token <aad-token>                             The Azure Active Directory authentication token.
                      --user-agent <user-agent>                           A label to identify this application when making requests to Azure Quantum.
                      --base-uri <base-uri>                               The base URI of the Azure Quantum endpoint.
                      --location <location>                               The location to use with the default endpoint.
                      --job-name <job-name>                               The name of the submitted job.
                      --job-params <job-params>                           Additional target-specific parameters for the submitted job (in the format \"key=value\").
                      --shots <shots>                                     The number of times the program is executed on the target machine.
                      --output <FriendlyUri|Id>                           The information to show in the output after the job is submitted.
                      --dry-run                                           Validate the program and options, but do not submit to Azure Quantum.
                      --verbose                                           Show additional information about the submission.
                      --submit-config-file <submit-config-file>           Configuration file to use custom submit properties.
                      -?, -h, --help                                      Show help and usage information"
    let given = testWithTarget "foo.target" "Help"
    given ["submit"; "--help"] |> yields message

[<Fact>]
let ``Supports simulating multiple entry points`` () =
    let given = test "Multiple entry points"
    given ["simulate"; "EntryPointTest.MultipleEntryPoints1"] |> yields "Hello from Entry Point 1!"
    given ["simulate"; "EntryPointTest.MultipleEntryPoints2"] |> yields "Hello from Entry Point 2!"
    given ["simulate"; "EntryPointTest3.MultipleEntryPoints3"] |> yields "Hello from Entry Point 3!"
    given ["simulate"] |> fails
    given [] |> fails

[<Fact>]
let ``Supports simulating multiple entry points with different parameters`` () =
    let given = test "Multiple entry points with different parameters"
    let entryPoint1Args = ["-n"; "42.5"]
    let entryPoint2Args = ["-s"; "Hello, World!"]
    let entryPoint3Args = ["-i"; "3"]
    
    given (["simulate"; "EntryPointTest.MultipleEntryPoints1"] @ entryPoint1Args) |> yields "42.5"
    given (["simulate"; "EntryPointTest.MultipleEntryPoints1"] @ entryPoint2Args) |> fails
    given (["simulate"; "EntryPointTest.MultipleEntryPoints1"] @ entryPoint3Args) |> fails
    given ["simulate"; "EntryPointTest.MultipleEntryPoints1"] |> fails
    
    given (["simulate"; "EntryPointTest.MultipleEntryPoints2"] @ entryPoint1Args) |> fails
    given (["simulate"; "EntryPointTest.MultipleEntryPoints2"] @ entryPoint2Args) |> yields "Hello, World!"
    given (["simulate"; "EntryPointTest.MultipleEntryPoints2"] @ entryPoint3Args) |> fails
    given ["simulate"; "EntryPointTest.MultipleEntryPoints2"] |> fails
    
    given (["simulate"; "EntryPointTest3.MultipleEntryPoints3"] @ entryPoint1Args) |> fails
    given (["simulate"; "EntryPointTest3.MultipleEntryPoints3"] @ entryPoint2Args) |> fails
    given (["simulate"; "EntryPointTest3.MultipleEntryPoints3"] @ entryPoint3Args) |> yields "3"
    given ["simulate"; "EntryPointTest3.MultipleEntryPoints3"] |> fails
    
    given ["simulate"] |> fails
    given [] |> fails

[<Fact>]
let ``Supports submitting multiple entry points`` () =
    let options =
        [
            "--subscription"
            "mySubscription"
            "--resource-group"
            "myResourceGroup"
            "--workspace"
            "myWorkspace"
            "--location"
            "location"
            "--target"
            "test.machine.noop"
        ]
    let given = test "Multiple entry points"
    let succeeds = yields "https://www.example.com/00000000-0000-0000-0000-0000000000000"
    given (["submit"; "EntryPointTest.MultipleEntryPoints1"] @ options) |> succeeds
    given (["submit"; "EntryPointTest.MultipleEntryPoints2"] @ options) |> succeeds
    given (["submit"; "EntryPointTest3.MultipleEntryPoints3"] @ options) |> succeeds
    given (["submit"] @ options) |> fails
    given [] |> fails

[<Fact>]
let ``Supports submitting multiple entry points with different parameters`` () =
    let options =
        [
            "--subscription"
            "mySubscription"
            "--resource-group"
            "myResourceGroup"
            "--workspace"
            "myWorkspace"
            "--location"
            "location"
            "--target"
            "test.machine.noop"
        ]
    let entryPoint1Args = ["-n"; "42.5"]
    let entryPoint2Args = ["-s"; "Hello, World!"]
    let entryPoint3Args = ["-i"; "3"]
    let given = test "Multiple entry points with different parameters"
    let succeeds = yields "https://www.example.com/00000000-0000-0000-0000-0000000000000"

    given (["submit"; "EntryPointTest.MultipleEntryPoints1"] @ entryPoint1Args @ options) |> succeeds
    given (["submit"; "EntryPointTest.MultipleEntryPoints1"] @ entryPoint2Args @ options) |> fails
    given (["submit"; "EntryPointTest.MultipleEntryPoints1"] @ entryPoint3Args @ options) |> fails
    given (["submit"; "EntryPointTest.MultipleEntryPoints1"] @ options) |> fails

    given (["submit"; "EntryPointTest.MultipleEntryPoints2"] @ entryPoint1Args @ options) |> fails
    given (["submit"; "EntryPointTest.MultipleEntryPoints2"] @ entryPoint2Args @ options) |> succeeds
    given (["submit"; "EntryPointTest.MultipleEntryPoints2"] @ entryPoint3Args @ options) |> fails
    given (["submit"; "EntryPointTest.MultipleEntryPoints2"] @ options) |> fails

    given (["submit"; "EntryPointTest3.MultipleEntryPoints3"] @ entryPoint1Args @ options) |> fails
    given (["submit"; "EntryPointTest3.MultipleEntryPoints3"] @ entryPoint2Args @ options) |> fails
    given (["submit"; "EntryPointTest3.MultipleEntryPoints3"] @ entryPoint3Args @ options) |> succeeds
    given (["submit"; "EntryPointTest3.MultipleEntryPoints3"] @ options) |> fails

    given submitWithNoOpTarget |> fails
    given [] |> fails
