// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2016  TS3AudioBot contributors
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace TS3AudioBot.ResourceFactories
{
	using System;
	using System.Collections.Generic;
	using System.Text.RegularExpressions;
	using System.Web.Script.Serialization;
	using Helper;

	public sealed class TwitchFactory : IResourceFactory
	{
		private Regex twitchMatch = new Regex(@"^(https?://)?(www\.)?twitch\.tv/(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private Regex m3u8ExtMatch = new Regex(@"#([\w-]+)(:(([\w-]+)=(""[^""]*""|[^,]+),?)*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
		private JavaScriptSerializer jsonParser;

		public TwitchFactory()
		{
			jsonParser = new JavaScriptSerializer();
		}

		public AudioType FactoryFor => AudioType.Twitch;

		public R<PlayResource> GetResource(string url)
		{
			var match = twitchMatch.Match(url);
			if (!match.Success)
				return RResultCode.TwitchInvalidUrl.ToString();
			return GetResourceById(new AudioResource(match.Groups[3].Value, null, AudioType.Twitch));
		}

		public R<PlayResource> GetResourceById(AudioResource resource)
		{
			var channel = resource.ResourceId;

			// request api token
			string jsonResponse;
			if (!WebWrapper.DownloadString(out jsonResponse, new Uri($"http://api.twitch.tv/api/channels/{channel}/access_token")))
				return RResultCode.NoConnection.ToString();

			var jsonDict = (Dictionary<string, object>)jsonParser.DeserializeObject(jsonResponse);

			// request m3u8 file
			var token = Uri.EscapeUriString(jsonDict["token"].ToString());
			var sig = jsonDict["sig"];
			// guaranteed to be random, chosen by fair dice roll.
			var random = 4;
			string m3u8;
			if (!WebWrapper.DownloadString(out m3u8, new Uri($"http://usher.twitch.tv/api/channel/hls/{channel}.m3u8?player=twitchweb&&token={token}&sig={sig}&allow_audio_only=true&allow_source=true&type=any&p={random}")))
				return RResultCode.NoConnection.ToString();

			// parse m3u8 file
			var dataList = new List<StreamData>();
			using (var reader = new System.IO.StringReader(m3u8))
			{
				var header = reader.ReadLine();
				if (string.IsNullOrEmpty(header) || header != "#EXTM3U")
					return RResultCode.TwitchMalformedM3u8File.ToString();

				while (true)
				{
					var blockInfo = reader.ReadLine();
					if (string.IsNullOrEmpty(blockInfo))
						break;

					var match = m3u8ExtMatch.Match(blockInfo);
					if (!match.Success)
						continue;

					switch (match.Groups[1].Value)
					{
					case "EXT-X-TWITCH-INFO": break; // Ignore twitch info line
					case "EXT-X-MEDIA":
						string streamInfo = reader.ReadLine();
						Match infoMatch;
						if (string.IsNullOrEmpty(streamInfo) ||
							 !(infoMatch = m3u8ExtMatch.Match(streamInfo)).Success ||
							 infoMatch.Groups[1].Value != "EXT-X-STREAM-INF")
							return RResultCode.TwitchMalformedM3u8File.ToString();

						var streamData = new StreamData();
						// #EXT-X-STREAM-INF:PROGRAM-ID=1,BANDWIDTH=128000,CODECS="mp4a.40.2",VIDEO="audio_only"
						for (int i = 0; i < infoMatch.Groups[3].Captures.Count; i++)
						{
							string key = infoMatch.Groups[4].Captures[i].Value.ToUpper();
							string value = infoMatch.Groups[5].Captures[i].Value;

							switch (key)
							{
							case "BANDWIDTH": streamData.Bandwidth = int.Parse(value); break;
							case "CODECS": streamData.Codec = TextUtil.StripQuotes(value); break;
							case "VIDEO":
								StreamQuality quality;
								if (Enum.TryParse(TextUtil.StripQuotes(value), out quality))
									streamData.QualityType = quality;
								else
									streamData.QualityType = StreamQuality.unknown;
								break;
							}
						}

						streamData.Url = reader.ReadLine();
						dataList.Add(streamData);
						break;
					default: break;
					}
				}
			}

			if (dataList.Count > 0)
				return new TwitchResource(dataList, resource.ResourceTitle != null ? resource : resource.WithName($"Twitch channel: {channel}"));
			else
				return RResultCode.TwitchNoStreamsExtracted.ToString();
		}

		public bool MatchLink(string uri) => twitchMatch.IsMatch(uri);

		public R<PlayResource> PostProcess(PlayData data)
		{
			var twResource = (TwitchResource)data.PlayResource;
			// TODO: selecting the best stream (better)
			int autoselectIndex = twResource.AvailableStreams.FindIndex(s => s.QualityType == StreamQuality.audio_only);
			if (autoselectIndex != -1)
			{
				twResource.Selected = autoselectIndex;
				return twResource;
			}

			// TODO add response like youtube
			return "The stream has no audio_only version.";
		}

		public string RestoreLink(string id) => "http://www.twitch.tv/" + id;

		public void Dispose() { }
	}

	public sealed class StreamData
	{
		public StreamQuality QualityType;
		public int Bandwidth;
		public string Codec;
		public string Url;
	}

	public enum StreamQuality
	{
		unknown,
		chunked,
		high,
		medium,
		low,
		mobile,
		audio_only,
	}

	public sealed class TwitchResource : PlayResource
	{
		public List<StreamData> AvailableStreams { get; private set; }
		public int Selected { get; set; }

		public TwitchResource(List<StreamData> availableStreams, AudioResource baseData) : base(baseData)
		{
			AvailableStreams = availableStreams;
		}

		public override string Play()
		{
			if (Selected < 0 && Selected >= AvailableStreams.Count)
				return null;
			Log.Write(Log.Level.Debug, "YT Playing: {0}", AvailableStreams[Selected]);
			return AvailableStreams[Selected].Url;
		}
	}
}
