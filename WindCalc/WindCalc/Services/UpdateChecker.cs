using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WindCalc.Services
{
    /// <summary>
    /// Polls GitHub Releases for a newer WindCalc version. If one exists, downloads
    /// the installer asset to %TEMP% so the shutdown prompt can launch it instantly.
    /// </summary>
    public static class UpdateChecker
    {
        // Owner/repo of the GitHub repository that publishes WindCalc releases.
        private const string GitHubOwner = "ConstructionCorps";
        private const string GitHubRepo  = "WindCalc";

        private static readonly HttpClient Http = BuildClient();

        private static HttpClient BuildClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("WindCalc-UpdateChecker");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version;

        public static async Task<UpdateInfo> CheckAsync()
        {
            var url  = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var rel  = JObject.Parse(json);

            var tag = (string)rel["tag_name"];
            if (string.IsNullOrWhiteSpace(tag)) return null;

            if (!TryParseVersion(tag, out var latest)) return null;
            if (latest <= CurrentVersion) return null;

            string downloadUrl = null;
            foreach (var asset in (JArray)rel["assets"])
            {
                var name = (string)asset["name"] ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = (string)asset["browser_download_url"];
                    break;
                }
            }
            if (string.IsNullOrEmpty(downloadUrl)) return null;

            var info = new UpdateInfo
            {
                Version      = latest,
                DownloadUrl  = downloadUrl,
                ReleaseNotes = Truncate((string)rel["body"] ?? "", 800),
            };

            info.LocalInstallerPath = await DownloadAsync(info).ConfigureAwait(false);
            return info;
        }

        private static async Task<string> DownloadAsync(UpdateInfo info)
        {
            var path = Path.Combine(Path.GetTempPath(), $"WindCalc-Setup-{info.Version}.exe");
            if (File.Exists(path)) return path;

            using (var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var dst = File.Create(path))
                {
                    await src.CopyToAsync(dst).ConfigureAwait(false);
                }
            }
            return path;
        }

        private static bool TryParseVersion(string tag, out Version version)
        {
            var m = Regex.Match(tag, @"\d+(\.\d+){1,3}");
            if (m.Success && Version.TryParse(m.Value, out version)) return true;
            version = null;
            return false;
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}
