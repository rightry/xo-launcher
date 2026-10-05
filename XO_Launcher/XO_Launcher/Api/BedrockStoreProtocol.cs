using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace XO_Launcher.Api
{
    /// <summary>
    /// Microsoft Store / Windows Update FE3 协议。做法对齐 BedrockBoot（BedrockLauncher.Core）
    /// 与 mc-w10-version-launcher：用版本库里的 UpdateID 换 APPX 直链。
    /// 需要该微软账号已购买 Minecraft for Windows；无账号时仍会尝试匿名请求。
    /// </summary>
    public static class BedrockStoreProtocol
    {
        private const string SecuredUrl = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx/secured";
        private const string DeviceAttributes =
            "E:BranchReadinessLevel=CBB&DchuNvidiaGrfxExists=1&ProcessorIdentifier=Intel64%20Family%206%20Model%2063%20Stepping%202&CurrentBranch=rs4_release&DataVer_RS5=1942&FlightRing=Retail&AttrDataVer=57&InstallLanguage=en-US&DchuAmdGrfxExists=1&OSUILocale=en-US&InstallationType=Client&FlightingBranchName=&Version_RS5=10&UpgEx_RS5=Green&GStatus_RS5=2&OSSkuId=48&App=WU&InstallDate=1529700913&ProcessorManufacturer=GenuineIntel&AppVer=10.0.17134.471&OSArchitecture=AMD64&UpdateManagementGroup=2&IsDeviceRetailDemo=0&IsFlightingEnabled=0&DchuIntelGrfxExists=1&TelemetryLevel=1&DefaultUserRegion=244&DeferFeatureUpdatePeriodInDays=365&Bios=Unknown&WuClientVer=10.0.17134.471&PausedFeatureStatus=1&OSVersion=10.0.17134.472&DeviceFamily=Windows.Desktop";

        private static readonly XNamespace Soap = "http://www.w3.org/2003/05/soap-envelope";
        private static readonly XNamespace Addressing = "http://www.w3.org/2005/08/addressing";
        private static readonly XNamespace SecExt = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        private static readonly XNamespace SecUtil = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
        private static readonly XNamespace WuWs = "http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization";
        private static readonly XNamespace WuClient = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XO-Launcher/1.0 (Minecraft launcher)");
            return client;
        }

        public static async Task<string> GetPackageUrlAsync(
            string updateId,
            string revisionNumber = "1",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(updateId))
                throw new InvalidOperationException("缺少 Store 更新标识，无法解析下载地址。");

            var xml = BuildDownloadRequest(updateId.Trim(), string.IsNullOrWhiteSpace(revisionNumber) ? "1" : revisionNumber);
            using var content = new StringContent(xml, Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml; charset=utf-8");
            using var response = await Client.PostAsync(SecuredUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Store 协议请求失败（" + (int)response.StatusCode + "）。请确认已购买 Minecraft for Windows，并已开启开发者模式。");

            var urls = ExtractUrls(body);
            var chosen = PickPackageUrl(urls);
            if (string.IsNullOrWhiteSpace(chosen))
                throw new InvalidOperationException("未能从 Store 解析到 APPX 下载地址。该版本可能已下架，或当前微软账号未拥有游戏。");
            return chosen;
        }

        private static string BuildDownloadRequest(string updateId, string revision)
        {
            var now = DateTime.UtcNow;
            var tickets = new XElement(WuWs + "WindowsUpdateTicketsToken",
                new XAttribute(SecUtil + "id", "ClientMSA"),
                new XAttribute(XNamespace.Xmlns + "wsu", SecUtil),
                new XAttribute(XNamespace.Xmlns + "wuws", WuWs),
                new XElement("TicketType", "",
                    new XAttribute("Name", "AAD"),
                    new XAttribute("Version", "1.0"),
                    new XAttribute("Policy", "MBI_SSL")));

            var envelope = new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "a", Addressing),
                new XAttribute(XNamespace.Xmlns + "s", Soap),
                new XElement(Soap + "Header",
                    new XElement(Addressing + "Action",
                        "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/GetExtendedUpdateInfo2",
                        new XAttribute(Soap + "mustUnderstand", 1)),
                    new XElement(Addressing + "MessageID", "urn:uuid:5754a03d-d8d5-489f-b24d-efc31b3fd32d"),
                    new XElement(Addressing + "To", SecuredUrl, new XAttribute(Soap + "mustUnderstand", 1)),
                    new XElement(SecExt + "Security",
                        new XAttribute(Soap + "mustUnderstand", 1),
                        new XAttribute(XNamespace.Xmlns + "o", SecExt),
                        new XElement(SecUtil + "Timestamp",
                            new XElement(SecUtil + "Created", now.ToString("o")),
                            new XElement(SecUtil + "Expires", now.AddMinutes(5).ToString("o"))),
                        tickets)),
                new XElement(Soap + "Body",
                    new XElement(WuClient + "GetExtendedUpdateInfo2",
                        new XElement(WuClient + "updateIDs",
                            new XElement(WuClient + "UpdateIdentity",
                                new XElement(WuClient + "UpdateID", updateId),
                                new XElement(WuClient + "RevisionNumber", revision))),
                        new XElement(WuClient + "infoTypes",
                            new XElement(WuClient + "XmlUpdateFragmentType", "FileUrl")),
                        new XElement(WuClient + "deviceAttributes", DeviceAttributes))));

            return new XDocument(envelope).ToString(SaveOptions.DisableFormatting);
        }

        private static string[] ExtractUrls(string soap)
        {
            try
            {
                var doc = XDocument.Parse(soap);
                return doc.Descendants(WuClient + "Url")
                    .Select(x => WebUtility.HtmlDecode(x.Value))
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private static string? PickPackageUrl(string[] urls)
        {
            if (urls.Length == 0) return null;
            static bool IsEncrypted(string url)
            {
                var path = url.Split('?', '#')[0];
                return path.EndsWith(".eappx", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".eappxbundle", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".emsix", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".emsixbundle", StringComparison.OrdinalIgnoreCase);
            }

            static bool IsPackage(string url)
            {
                var path = url.Split('?', '#')[0];
                return path.EndsWith(".appx", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase);
            }

            var packages = urls.Where(u => !IsEncrypted(u) && (IsPackage(u) || u.Contains("tlu.dl.", StringComparison.OrdinalIgnoreCase))).ToArray();
            return packages.FirstOrDefault(u => u.Contains("tlu.dl.", StringComparison.OrdinalIgnoreCase))
                   ?? packages.LastOrDefault()
                   ?? urls.FirstOrDefault(u => !IsEncrypted(u));
        }
    }
}
