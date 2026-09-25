using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Loungepad.Interop;

namespace Loungepad.Services;

/// <summary>What the Settings row and the tray menu draw. <see cref="State"/> is one of idle,
/// checking, upToDate, available, downloading, ready, failed or unsupported.</summary>
public sealed class UpdateStatus
{
    public string State { get; set; } = "idle";
    public string Current { get; set; } = "";
    public string? Latest { get; set; }
    /// <summary>0..100 while downloading.</summary>
    public int Progress { get; set; }
    /// <summary>Why it failed, or why this copy cannot update itself.</summary>
    public string? Message { get; set; }
    public DateTime? CheckedAt { get; set; }
    /// <summary>The current step was started from Settings or the tray rather than by the timer,
    /// so the page does not announce an answer somebody is already looking at.</summary>
    public bool UserAsked { get; set; }
}

/// <summary>
/// Updates from the GitHub releases, the same zip a person would download by hand.
///
/// The release's zip is fetched, checked against the size and SHA-256 GitHub publishes for it,
/// and unpacked into %LOCALAPPDATA%\Loungepad\updates. Installing is then a swap in the folder the
/// app was unzipped into: every file the release carries is renamed aside and the new one copied
/// in its place, the new exe is started, and this one exits. Windows lets a running exe be
/// renamed, though not deleted, so nothing has to wait for this process to be gone first; the
/// set-aside files are deleted by the next start (<see cref="FinishPreviousUpdate"/>).
///
/// With automatic updates on, a new version is downloaded in the background and installed the
/// next time the app starts -- at login, usually -- because installing means a restart and the
/// launcher is the thing on the TV. Settings and the tray can install it at once. With them off,
/// nothing is fetched until somebody asks.
///
/// Only a release build updates itself. A dev build (the dll beside the exe) would be replaced by
/// the release, and a folder that cannot be written (Program Files) cannot be updated in place;
/// both say so rather than failing halfway.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Repo = "sukumar-v/Loungepad";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);
    /// <summary>Out of the way of the library scan and the metadata pass, which own the first minute.</summary>
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    /// <summary>What a replaced file is renamed to until the next start deletes it. Specific
    /// enough that nothing else in the folder can match it.</summary>
    public const string SetAsideSuffix = ".loungepad-old";

    public static string UpdatesDir => Path.Combine(Paths.LocalDir, "updates");
    private static string StagedFile => Path.Combine(UpdatesDir, "staged.json");
    private static string InstallDir => AppContext.BaseDirectory;
    private static string ExeName => Path.GetFileName(Environment.ProcessPath ?? "Loungepad.exe");

    private readonly Func<bool> _auto;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _busy;
    private Release? _release;

    public UpdateStatus Status { get; }
    public static Version Current { get; } = ReadCurrent();
    public event Action<UpdateStatus>? Changed;

    public UpdateService(Func<bool> autoEnabled)
    {
        _auto = autoEnabled;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"Loungepad/{Format(Current)} (+https://github.com/{Repo})");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

        Status = new UpdateStatus { Current = Format(Current) };
        if (WhyNot() is { } reason) { Status.State = "unsupported"; Status.Message = reason; }
        else if (ReadStaged() is { } staged)
        {
            // Downloaded by an earlier run and not installed, because automatic updates were
            // turned off in between. It is still good; offer it.
            Status.State = "ready";
            Status.Latest = Format(staged.Version);
        }
    }

    /// <summary>The first check a minute in, then every six hours. Each tick asks the setting,
    /// so turning automatic updates off stops the next one without restarting anything.</summary>
    public void Start()
    {
        _timer = new System.Threading.Timer(_ =>
        {
            if (_auto()) _ = CheckAsync(userAsked: false);
        }, null, FirstCheck, CheckEvery);
    }

    /// <summary>Ask GitHub for the latest release. With automatic updates on, a newer one is
    /// downloaded straight away.</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (Status.State == "unsupported") return;
        lock (_gate)
        {
            // A download that finished is not undone by asking again, and one in flight is not
            // started twice.
            if (_busy || Status.State == "ready") { if (userAsked) Raise(); return; }
            _busy = true;
        }
        try
        {
            Status.UserAsked = userAsked;
            Set("checking");
            var release = await FetchLatestAsync();
            Status.CheckedAt = DateTime.Now;
            if (release is null) return;
            _release = release;
            Status.Latest = Format(release.Version);
            if (release.Version <= Current) { Set("upToDate"); return; }
            Set("available");
            Log.Info($"Update: {Status.Latest} is available (running {Status.Current})");
        }
        finally { lock (_gate) _busy = false; }

        if (_auto() && !userAsked) await DownloadAsync(userAsked: false);
    }

    /// <summary>Download and unpack the release found by the last check, ready to install.</summary>
    public async Task DownloadAsync(bool userAsked)
    {
        lock (_gate)
        {
            if (_busy || _release is null || Status.State != "available") return;
            _busy = true;
        }
        var release = _release;
        var zip = Path.Combine(UpdatesDir, release.AssetName);
        var part = zip + ".part";
        try
        {
            Status.UserAsked = userAsked;
            Status.Progress = 0;
            Set("downloading");
            Directory.CreateDirectory(UpdatesDir);

            using (var response = await _http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? release.AssetSize;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(part);
                var buffer = new byte[81920];
                long done = 0;
                int read, shown = -1;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    var pct = total > 0 ? (int)(done * 100 / total) : 0;
                    if (pct != shown) { shown = pct; Status.Progress = pct; Raise(); }
                }
            }

            // GitHub publishes the size, and a SHA-256 of every asset uploaded since mid-2025.
            // A truncated download is the likely failure; a checksum is what catches the rest.
            var length = new FileInfo(part).Length;
            if (release.AssetSize > 0 && length != release.AssetSize)
                throw new InvalidDataException($"the download was {length} bytes, not {release.AssetSize}");
            if (release.Sha256 is { } expected)
            {
                string actual;
                await using (var fs = File.OpenRead(part))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
                if (actual != expected) throw new InvalidDataException("the download does not match its checksum");
            }
            File.Move(part, zip, overwrite: true);

            var dir = Path.Combine(UpdatesDir, Format(release.Version));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            // ExtractToDirectory refuses an entry that would land outside the folder.
            ZipFile.ExtractToDirectory(zip, dir);
            File.Delete(zip);
            if (Validate(dir) is { } problem) throw new InvalidDataException(problem);

            File.WriteAllText(StagedFile, JsonSerializer.Serialize(new StagedUpdate { Version = Format(release.Version), Dir = dir }));
            Log.Info($"Update: {Status.Latest} downloaded to {dir}");
            Set("ready");
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            Fail($"Could not download {Status.Latest}: {Describe(ex)}");
        }
        finally { lock (_gate) _busy = false; }
    }

    /// <summary>
    /// Swap the downloaded version in and start it. Returns true when the new copy has been
    /// started and this one should exit now; false with the reason in the status otherwise, in
    /// which case every file is back as it was.
    /// </summary>
    public bool InstallAndRestart(IEnumerable<string> args)
    {
        var staged = ReadStaged();
        if (staged is null) { Fail("The downloaded update is missing. Check for updates again"); return false; }
        if (!Install(staged, args, out var error)) { Fail(error); return false; }
        return true;
    }

    /// <summary>
    /// Install an update downloaded by an earlier run, before anything else starts. Returns true
    /// when the new version has been started and this process should exit at once.
    /// </summary>
    public static bool InstallStagedAtStartup(IEnumerable<string> args)
    {
        if (WhyNot() is not null) return false;
        var staged = ReadStaged();
        if (staged is null) return false;
        Log.Info($"Update: installing {Format(staged.Version)}, downloaded earlier");
        if (Install(staged, args, out var error)) return true;
        // Give up on this download rather than failing the same way at every start; the next
        // check fetches it again.
        Log.Info($"Update: {error}");
        Discard();
        return false;
    }

    /// <summary>
    /// The start after an update: delete what the swap set aside (the old exe could not be
    /// deleted while it was the one running) and any download older than what is running now.
    /// </summary>
    public static void FinishPreviousUpdate()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(InstallDir, "*" + SetAsideSuffix, SearchOption.AllDirectories))
            {
                try { File.Delete(f); } catch { /* still held; the next start tries again */ }
            }
        }
        catch { }
        if (ReadStaged() is null && Directory.Exists(UpdatesDir)) Discard();
    }

    // ---- the swap ----

    private static bool Install(Staged staged, IEnumerable<string> args, out string error)
    {
        var installed = new List<(string Target, bool HadOld)>();
        try
        {
            foreach (var src in Directory.EnumerateFiles(staged.Dir, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(InstallDir, Path.GetRelativePath(staged.Dir, src));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var hadOld = File.Exists(target);
                if (hadOld) File.Move(target, target + SetAsideSuffix, overwrite: true);
                installed.Add((target, hadOld));
                File.Copy(src, target);
            }
        }
        catch (Exception ex)
        {
            // Put back everything already swapped, newest first, so a failure halfway never
            // leaves a folder with half of each version in it.
            for (var i = installed.Count - 1; i >= 0; i--)
            {
                var (target, hadOld) = installed[i];
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (hadOld) File.Move(target + SetAsideSuffix, target);
                }
                catch { /* nothing more to try */ }
            }
            error = $"Could not install {Format(staged.Version)}: {Describe(ex)}";
            return false;
        }

        try
        {
            var exe = Path.Combine(InstallDir, ExeName);
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = InstallDir };
            foreach (var a in args) info.ArgumentList.Add(a);
            info.ArgumentList.Add("--updated-from");
            info.ArgumentList.Add(Format(Current));
            info.ArgumentList.Add("--wait-for");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            using var p = Process.Start(info);
            // It is about to wait for this process to exit and then show a window, by which time
            // it would no longer have been started by the foreground process.
            if (p is not null) NativeMethods.AllowSetForegroundWindow((uint)p.Id);
        }
        catch (Exception ex)
        {
            // The files are in, only the start failed: the new version runs on the next start.
            error = $"{Format(staged.Version)} is installed but could not be started: {ex.Message}. Start Loungepad again";
            Discard();
            return false;
        }

        Log.Info($"Update: installed {Format(staged.Version)} over {Format(Current)}; restarting");
        Discard();
        error = "";
        return true;
    }

    /// <summary>
    /// The new copy's first step: wait for the one that installed it to exit, and for its WebView2
    /// to let go of the profile. A browser process outlives its host by a moment, and a second
    /// environment on a profile that is still locked fails to start -- which here would be a
    /// launcher that updates itself into a black screen.
    /// </summary>
    public static void WaitForPrevious(int pid)
    {
        try
        {
            using var old = Process.GetProcessById(pid);
            old.WaitForExit(30_000);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { }

        var lockFile = Path.Combine(Paths.WebViewDir, "EBWebView", "lockfile");
        var until = DateTime.UtcNow.AddSeconds(5);
        while (File.Exists(lockFile) && DateTime.UtcNow < until)
        {
            try { using (File.Open(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } break; }
            catch (IOException) { Thread.Sleep(100); }
            catch { break; }
        }
    }

    // ---- GitHub ----

    private sealed record Release(Version Version, string AssetName, string AssetUrl, long AssetSize, string? Sha256);

    private async Task<Release?> FetchLatestAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (response.StatusCode == HttpStatusCode.NotFound) { Fail("No releases have been published yet"); return null; }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                // 60 requests an hour per address without a key, shared with anything else on
                // the network that asks.
                Fail("GitHub is limiting requests from this network. Try again in an hour");
                return null;
            }
            response.EnsureSuccessStatusCode();
            var node = JsonNode.Parse(await response.Content.ReadAsStringAsync());
            var tag = node?["tag_name"]?.GetValue<string>();
            if (ParseVersion(tag) is not { } version) { Fail($"The latest release has no version number ({tag})"); return null; }

            var suffix = $"-win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}.zip";
            foreach (var asset in node!["assets"]?.AsArray() ?? new JsonArray())
            {
                var name = asset?["name"]?.GetValue<string>();
                var url = asset?["browser_download_url"]?.GetValue<string>();
                if (name is null || url is null || !name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                // Only a plain file name: it becomes a path under the updates folder.
                if (name != Path.GetFileName(name)) continue;
                var size = asset!["size"]?.GetValue<long>() ?? 0;
                var digest = asset["digest"]?.GetValue<string>();
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest["sha256:".Length..].ToLowerInvariant() : null;
                return new Release(version, name, url, size, sha);
            }
            if (version <= Current) return new Release(version, "", "", 0, null);
            Fail($"Loungepad {Format(version)} has no download for this PC");
            return null;
        }
        catch (Exception ex)
        {
            Fail($"Could not check for updates: {Describe(ex)}");
            return null;
        }
    }

    // ---- bookkeeping ----

    private sealed class StagedUpdate
    {
        public string Version { get; set; } = "";
        public string Dir { get; set; } = "";
    }

    private sealed record Staged(Version Version, string Dir);

    /// <summary>A downloaded update that is newer than this copy and still complete on disk.</summary>
    private static Staged? ReadStaged()
    {
        try
        {
            if (!File.Exists(StagedFile)) return null;
            var s = JsonSerializer.Deserialize<StagedUpdate>(File.ReadAllText(StagedFile));
            if (s is null || ParseVersion(s.Version) is not { } v || v <= Current) return null;
            // Only ever a folder we unpacked into, whatever the file says.
            var dir = Path.GetFullPath(s.Dir);
            if (!dir.StartsWith(Path.GetFullPath(UpdatesDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            return Validate(dir) is null ? new Staged(v, dir) : null;
        }
        catch { return null; }
    }

    private static string? Validate(string dir)
    {
        if (!File.Exists(Path.Combine(dir, ExeName))) return $"the download has no {ExeName}";
        if (!File.Exists(Path.Combine(dir, "ui", "index.html"))) return "the download has no ui folder";
        return null;
    }

    /// <summary>Everything under the updates folder goes: a download that was installed, one
    /// that is older than what is running, and one that could not be installed.</summary>
    private static void Discard()
    {
        try { if (Directory.Exists(UpdatesDir)) Directory.Delete(UpdatesDir, recursive: true); }
        catch { /* a file in use; the next start tries again */ }
    }

    /// <summary>Why this copy cannot update itself, or null when it can.</summary>
    private static string? WhyNot()
    {
        // A single-file build has no assembly location. Anything else is a dotnet build output,
        // and replacing it with the release would throw away whatever is being worked on.
        // (IL3000 warns that Location is empty in a single-file app, which is the point here.)
#pragma warning disable IL3000
        if (!string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location))
#pragma warning restore IL3000
            return "This is a development build. Updates are for the releases on GitHub";
        try
        {
            var probe = Path.Combine(InstallDir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch
        {
            return $"Loungepad cannot write to its own folder ({InstallDir.TrimEnd('\\')}). Move it somewhere like Documents to get updates";
        }
    }

    private static Version ReadCurrent()
    {
        var v = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>"v1.5.0" or "1.5.0" to a three-part version; a pre-release suffix is dropped.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "no connection to GitHub",
        TaskCanceledException => "GitHub took too long to answer",
        _ => ex.Message,
    };

    private void Set(string state)
    {
        Status.State = state;
        if (state != "failed") Status.Message = null;
        Raise();
    }

    private void Fail(string message)
    {
        Log.Info($"Update: {message}");
        Status.State = "failed";
        Status.Message = message;
        Raise();
    }

    private void Raise() => Changed?.Invoke(Status);

    public void Dispose()
    {
        _timer?.Dispose();
        _http.Dispose();
    }
}
