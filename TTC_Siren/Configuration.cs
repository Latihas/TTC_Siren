using System;
using Dalamud.Configuration;
using TtcServer.config;

namespace TTC;

[Serializable]
public class Configuration : IPluginConfiguration {
	public int Version { get; set; } = 0;
	public UnknownCardConfig UnknownCardConfig { get; set; } = new();
	public void Save() {
		Plugin.PluginInterface.SavePluginConfig(this);
	}
}