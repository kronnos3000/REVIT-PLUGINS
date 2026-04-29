using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace CCorpPrint.Services
{
    /// <summary>
    /// Polls GitHub Releases for a newer CCorpPrint installer and pre-downloads it
    /// to %TEMP% so the shutdown prompt can launch instantly. Looks for an asset
    /// whose name starts with "CCorpPrint-Setup" so it doesn't pick up WindCalc
    /// installers from the same repo.
    /// </summary>
    public static class UpdateChecker
    {
        private const string GitHubOwner = "kronnos3000";
        private const string GitHubRepo  = "REVIT-PLUGINS";
        private const string AssetPrefix = "CCorpPrint-Setup";

        private static readonly HttpClient Http = BuildClient();

        private static HttpClient BuildClient()
        {
            var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("CCorpPrint-UpdateChecker");
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }

        public static Version CurrentVersion =>
            Assembly.GetExecutingAssembly().GetName().Version;

        public static async Task<UpdateInfo> CheckAsync()
        {
            // The repo hosts releases for multiple addins. Walk recent releases
            // and pick the latest tagged one whose assets contain a matching prefix.
            var url  = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases?per_page=20";
            var json = await Http.GetStringAsync(url).ConfigureAwait(false);
            var arr  = JArray.Parse(json);

            foreach (var rel in arr)
            {
                var tag = (string)rel["tag_name"];
                if (string.IsNullOrWhiteSpace(tag)) continue;
                if (!TryParseVersion(tag, out var ver)) continue;
                if (ver <= CurrentVersion) continue;

                string downloadUrl = null;
                foreach (var asset in (JArray)rel["assets"] ?? new JArray())
                {
                    var name = (string)asset["name"] ?? "";
                    if (name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        downloadUrl = (string)asset["browser_download_url"];
                        break;
                    }
                }
                if (string.IsNullOrEmpty(downloadUrl)) continue;

                var info = new UpdateInfo
                {
                    Version      = ver,
                    DownloadUrl  = downloadUrl,
                    ReleaseNotes = Truncate((string)rel["body"] ?? "", 800),
                };
                info.LocalInstallerPath = await DownloadAsync(info).ConfigureAwait(false);
                return info;
            }

            return null;
        }

        private static async Task<string> DownloadAsync(UpdateInfo info)
        {
            var path = Path.Combine(Path.GetTempPath(), $"{AssetPrefix}-{info.Version}.exe");
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
