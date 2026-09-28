using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace GameBase.Tests;

/// <summary>
/// Finds every <see cref="TestSuite"/> in the assembly, runs its tests, prints
/// the results and quits with exit code 0 when everything passed, 1 otherwise.
///
/// Run headless from the project folder:
///   godot --headless --path . res://Tests/TestRunner.tscn
/// Add <c>-- --filter=Name</c> to run only suites or tests whose name contains Name.
/// </summary>
public partial class TestRunner : Node
{
    public override void _Ready() => _ = RunAll();

    private async Task RunAll()
    {
        string filter = Filter();
        int passed = 0;
        var failures = new List<string>();

        IEnumerable<Type> suites = typeof(TestRunner).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TestSuite)) && !t.IsAbstract)
            .OrderBy(t => t.Name);

        GD.Print("=== TESTS ===");

        foreach (Type suite in suites)
        {
            IEnumerable<MethodInfo> tests = suite.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .OrderBy(m => m.MetadataToken);

            foreach (MethodInfo test in tests)
            {
                string name = $"{suite.Name}.{test.Name}";

                if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                string error = await Run(suite, test);

                if (error == null)
                {
                    passed++;
                    GD.Print($"  pass  {name}");
                }
                else
                {
                    failures.Add($"{name}: {error}");
                    GD.Print($"  FAIL  {name}: {error}");
                }
            }
        }

        GD.Print($"=== {passed} passed, {failures.Count} failed ===");
        GetTree().Quit(failures.Count == 0 ? 0 : 1);
    }

    private async Task<string> Run(Type suiteType, MethodInfo test)
    {
        var root = new Node { Name = test.Name };
        AddChild(root);

        try
        {
            var suite = (TestSuite)Activator.CreateInstance(suiteType);
            suite.Root = root;

            object result = test.Invoke(suite, null);
            if (result is Task task)
                await task;

            return null;
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
        {
            return Describe(wrapped.InnerException);
        }
        catch (Exception error)
        {
            return Describe(error);
        }
        finally
        {
            root.Free();
        }
    }

    private static string Describe(Exception error) =>
        error is TestFailure ? error.Message : error.ToString();

    private static string Filter()
    {
        foreach (string arg in OS.GetCmdlineUserArgs())
        {
            if (arg.StartsWith("--filter="))
                return arg["--filter=".Length..];
        }

        return null;
    }
}
