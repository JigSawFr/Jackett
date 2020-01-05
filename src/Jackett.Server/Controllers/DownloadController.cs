﻿using BencodeNET.Parsing;
using Jackett.Common.Models.Config;
using Jackett.Common.Services.Interfaces;
using Jackett.Common.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using NLog;
using System;
using System.Text;
using System.Threading.Tasks;

namespace Jackett.Server.Controllers
{
    [AllowAnonymous]
    [ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
    [Route("dl/{indexerID}")]
    public class DownloadController : Controller
    {
        private ServerConfig serverConfig;
        private Logger logger;
        private IIndexerManagerService indexerService;
        private IProtectionService protectionService;

        public DownloadController(IIndexerManagerService i, Logger l, IProtectionService ps, ServerConfig sConfig)
        {
            serverConfig = sConfig;
            logger = l;
            indexerService = i;
            protectionService = ps;
        }

        [HttpGet]
        public async Task<IActionResult> Download(string indexerID, string path, string jackett_apikey, string file)
        {
            try
            {
                if (serverConfig.APIKey != jackett_apikey)
                    return Unauthorized();

                var indexer = indexerService.GetWebIndexer(indexerID);

                if (!indexer.IsConfigured)
                {
                    logger.Warn(string.Format("Rejected a request to {0} which is unconfigured.", indexer.DisplayName));
                    return Forbid("This indexer is not configured.");
                }

                path = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(path));
                path = protectionService.UnProtect(path);

                var target = new Uri(path, UriKind.RelativeOrAbsolute);
                var downloadBytes = await indexer.Download(target);

                // handle magnet URLs
                if (downloadBytes.Length >= 7
                    && downloadBytes[0] == 0x6d // m
                    && downloadBytes[1] == 0x61 // a
                    && downloadBytes[2] == 0x67 // g
                    && downloadBytes[3] == 0x6e // n
                    && downloadBytes[4] == 0x65 // e
                    && downloadBytes[5] == 0x74 // t
                    && downloadBytes[6] == 0x3a // :
                    )
                {
                    // some sites provide magnet links with non-ascii characters, the only way to be sure the url
                    // is well encoded is to unscape and escape again
                    // https://github.com/Jackett/Jackett/issues/5372
                    // https://github.com/Jackett/Jackett/issues/4761
                    var magneturi = Uri.EscapeUriString(Uri.UnescapeDataString(Encoding.UTF8.GetString(downloadBytes)));
                    return Redirect(magneturi);
                }

                // This will fix torrents where the keys are not sorted, and thereby not supported by Sonarr.
                byte[] sortedDownloadBytes = null;
                try
                {
                    var parser = new BencodeParser();
                    var torrentDictionary = parser.Parse(downloadBytes);
                    sortedDownloadBytes = torrentDictionary.EncodeAsBytes();
                }
                catch (Exception e)
                {
                    var content = indexer.Encoding.GetString(downloadBytes);
                    logger.Error(content);
                    throw new Exception("BencodeParser failed", e);
                }

                string fileName = StringUtil.MakeValidFileName(file, '_', false) + ".torrent"; // call MakeValidFileName again to avoid any kind of injection attack

                return File(sortedDownloadBytes, "application/x-bittorrent", fileName);
            }
            catch (Exception e)
            {
                logger.Error(e, "Error downloading " + indexerID + " " + path);
                return NotFound();
            }
        }
    }
}
