using System.Diagnostics;
using Msync.Cli;

if (args.Length != 1) throw new ArgumentException("Pass the actual msync executable to exercise its native SSH askpass helper.");
string binary = Path.GetFullPath(args[0]);
List<SshPrompt> prompts = [];
string? answer = "pássphrase with spaces and \"quotes\"";
await using (SshPromptBroker broker = new((prompt, _) => { prompts.Add(prompt); return Task.FromResult<string?>(answer); }, "ssh://fixture", CancellationToken.None))
{
    foreach (var (hint, kind, text) in new[]
    {
        ("", "secret", "Enter passphrase for key '/home/Klee/.ssh/id_ed25519': "),
        ("", "confirm", "The authenticity of host 'fixture' cannot be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? "),
        ("confirm", "confirm", "The authenticity of host 'fixture' cannot be established.\nAre you sure you want to continue connecting?"),
        ("none", "notice", "Confirm user presence for key ED25519-SK SHA256:fixture")
    })
    {
        answer = kind == "confirm" ? "yes" : kind == "notice" ? "" : "pássphrase with spaces and \"quotes\"";
        var result = await InvokeAsync(binary, broker, text, hint);
        Check(result.ExitCode == 0 && result.Output == answer && result.Error == "", "actual helper answer bytes");
        Check(prompts[^1] is { Endpoint: "ssh://fixture" } observed && observed.Prompt == text && observed.Kind == kind, "native prompt context");
    }
    Check(prompts.Select(prompt => prompt.Id).Distinct().Count() == 4, "prompt identities are not reused");
    answer = null;
    var cancelled = await InvokeAsync(binary, broker, "Password:", "");
    Check(cancelled.ExitCode == 1 && cancelled.Output == "", "dismissal is not an empty password");

    ProcessStartInfo configured = new(binary);
    configured.Environment["SSH_ASKPASS"] = "/existing/askpass";
    broker.Configure(configured);
    Check(configured.Environment["SSH_ASKPASS"] == "/existing/askpass" && !configured.Environment.ContainsKey("MSYNC_SSH_ASKPASS_PIPE"), "explicit native askpass remains owned by its configuration");
}

TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
using CancellationTokenSource lifetime = new();
await using (SshPromptBroker broker = new(async (_, cancellationToken) =>
{
    admitted.TrySetResult();
    await Task.Delay(Timeout.Infinite, cancellationToken);
    return null;
}, "ssh://cancelled", lifetime.Token))
{
    Task<HelperResult> helper = InvokeAsync(binary, broker, "Password:", "");
    await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await lifetime.CancelAsync();
    HelperResult result = await helper.WaitAsync(TimeSpan.FromSeconds(10));
    Check(result.ExitCode == 1 && result.Output == "", "session cancellation retires the owned helper");
}
Console.WriteLine("PASS: actual SSH askpass helper, repeated native prompts, answer bytes, dismissal, configuration ownership and cancellation");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<HelperResult> InvokeAsync(string binary, SshPromptBroker broker, string prompt, string hint)
{
    ProcessStartInfo start = new(binary) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.Environment.Remove("SSH_ASKPASS");
    broker.Configure(start);
    start.Environment["SSH_ASKPASS_PROMPT"] = hint;
    start.ArgumentList.Add(prompt);
    using Process process = Process.Start(start) ?? throw new IOException("Cannot launch the actual SSH askpass helper.");
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    return new(process.ExitCode, await output, await error);
}

internal sealed record HelperResult(int ExitCode, string Output, string Error);
