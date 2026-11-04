using System.Reflection;
using System.Text;

namespace ClipLine.Tests;

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}

public static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{message}：期望 {expected}，实际 {actual}");
    }

    public static void Near(double expected, double actual, double epsilon, string message)
    {
        if (Math.Abs(expected - actual) > epsilon)
            throw new AssertionException($"{message}：期望 {expected}±{epsilon}，实际 {actual}");
    }

    public static void Contains(string needle, string? haystack, string message) =>
        True(haystack?.Contains(needle, StringComparison.Ordinal) == true,
            $"{message}：未在「{haystack}」中找到「{needle}」");
}

public interface ITestSuite
{
    void Register(TestRunner runner);
}

public sealed class TestRunner
{
    private readonly List<(string Id, string Name, Action Run)> _cases = new();
    private readonly List<(string Id, string Name, bool Passed, string Detail)> _results = new();

    public void Case(string id, string name, Action run) => _cases.Add((id, name, run));

    public IReadOnlyList<(string Id, string Name, bool Passed, string Detail)> Results => _results;

    public int Run(string? reportPath)
    {
        foreach (var (id, name, run) in _cases)
        {
            try
            {
                run();
                _results.Add((id, name, true, string.Empty));
                Console.WriteLine($"PASS  {id,-10} {name}");
            }
            catch (Exception ex)
            {
                _results.Add((id, name, false, ex.Message));
                Console.WriteLine($"FAIL  {id,-10} {name}  -> {ex.Message}");
            }
        }

        int passed = _results.Count(r => r.Passed);
        Console.WriteLine(new string('-', 64));
        Console.WriteLine($"TOTAL {_results.Count}   PASS {passed}   FAIL {_results.Count - passed}");

        if (!string.IsNullOrEmpty(reportPath))
        {
            string full = Path.GetFullPath(reportPath);
            string? directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var sb = new StringBuilder();
            sb.AppendLine("id,name,result,detail");
            foreach (var r in _results)
            {
                string csv = $"{r.Id},\"{r.Name.Replace("\"", "\"\"")}\",{(r.Passed ? "PASS" : "FAIL")},\"{r.Detail.Replace("\"", "\"\"")}\"";
                sb.AppendLine(csv);
            }
            File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"REPORT {reportPath}");
        }

        return _results.Count - passed == 0 ? 0 : 1;
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* 重定向到文件时忽略 */ }
        var runner = new TestRunner();
        var suites = typeof(Program).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(ITestSuite).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        foreach (var type in suites)
        {
            if (Activator.CreateInstance(type) is ITestSuite suite) suite.Register(runner);
        }

        string? report = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--report") report = args[i + 1];
        }
        return runner.Run(report);
    }
}
