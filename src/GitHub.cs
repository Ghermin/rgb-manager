using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace RgbManager;

/// <summary>Dernière release d'un dépôt GitHub, et téléchargement vérifié (taille, SHA-256) de l'un de ses fichiers.</summary>
static class GitHub
{
    public sealed class Asset
    {
        public Version Version;
        public string Url;
        public long Size;
        public string Sha256; // null si GitHub ne le donne pas
    }

    /// <summary>Le fichier <paramref name="assetName"/> de la dernière release, ou null s'il n'y en a pas.</summary>
    public static Asset Latest(string repo, string assetName)
    {
        string json;
        using (var web = NewClient(api: true)) json = web.DownloadString($"https://api.github.com/repos/{repo}/releases/latest");
        var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
        if (!Version.TryParse((root["tag_name"] as string ?? "").TrimStart('v'), out var version)) return null;
        foreach (var item in (object[])root["assets"])
        {
            var a = (Dictionary<string, object>)item;
            if (a["name"] as string != assetName) continue;
            string digest = a.TryGetValue("digest", out var d) ? d as string : null;
            return new Asset
            {
                Version = Normalize(version),
                Url = (string)a["browser_download_url"],
                Size = Convert.ToInt64(a["size"]),
                Sha256 = digest != null && digest.StartsWith("sha256:") ? digest.Substring(7).ToLowerInvariant() : null,
            };
        }
        return null;
    }

    /// <summary>Télécharge le fichier dans <paramref name="path"/>. Lève une exception s'il est incomplet ou altéré.</summary>
    public static void Download(Asset asset, string path)
    {
        string partial = path + ".part";
        File.Delete(partial);
        using (var web = NewClient(api: false)) web.DownloadFile(asset.Url, partial);
        if (new FileInfo(partial).Length != asset.Size)
        {
            File.Delete(partial);
            throw new IOException("téléchargement incomplet");
        }
        if (asset.Sha256 != null)
        {
            string hash;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(partial))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (hash != asset.Sha256)
            {
                File.Delete(partial);
                throw new IOException("empreinte SHA-256 différente de celle annoncée par GitHub");
            }
        }
        File.Delete(path);
        File.Move(partial, path);
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    static WebClient NewClient(bool api)
    {
        // Sans attribut TargetFramework dans l'exe, .NET 4.8 n'active pas TLS 1.2 de lui-même : GitHub l'exige.
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        var web = new WebClient();
        web.Headers[HttpRequestHeader.UserAgent] = "RgbManager";
        if (api) web.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
        return web;
    }
}
