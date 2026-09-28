using Godot;
using System;
using System.Threading.Tasks;

namespace GameBase.Tests;

/// <summary>Marks a method of a <see cref="TestSuite"/> as a test. It may return void or Task.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
}

/// <summary>Thrown by a failed check; the runner reports its message.</summary>
public sealed class TestFailure : Exception
{
    public TestFailure(string message) : base(message)
    {
    }
}

/// <summary>
/// A group of tests. Every public <see cref="TestAttribute"/> method runs with a
/// fresh instance of the suite, so tests never share state. Anything a test adds
/// under <see cref="Root"/> is freed when it finishes.
/// </summary>
public abstract class TestSuite
{
    /// <summary>A node in the running scene tree that tests may add children to.</summary>
    public Node Root { get; internal set; }

    protected SceneTree Tree => Root.GetTree();

    protected static void Check(bool condition, string message)
    {
        if (!condition)
            throw new TestFailure(message);
    }

    protected static void Near(float actual, float expected, float tolerance, string what)
    {
        if (Mathf.Abs(actual - expected) > tolerance)
            throw new TestFailure($"{what}: expected {expected} +/- {tolerance}, got {actual}");
    }

    protected static void Equal<T>(T actual, T expected, string what)
    {
        if (!Equals(actual, expected))
            throw new TestFailure($"{what}: expected {expected}, got {actual}");
    }

    /// <summary>Adds a node under the test root, so it is freed with the test.</summary>
    protected T Add<T>(T node) where T : Node
    {
        Root.AddChild(node);
        return node;
    }

    /// <summary>Waits for the physics server to take in shapes added this frame.</summary>
    protected async Task PhysicsFrames(int count = 2)
    {
        for (int n = 0; n < count; n++)
            await Root.ToSignal(Tree, SceneTree.SignalName.PhysicsFrame);
    }
}
