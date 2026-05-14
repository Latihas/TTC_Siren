using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using TtcServer;

namespace TTC;

public sealed class Plugin : IDalamudPlugin {
	[PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
	[PluginService] public static IPluginLog Log { get; private set; } = null!;
	[PluginService] public static IChatGui Chat { get; private set; } = null!;
	[PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
	[PluginService] internal static IFramework Framework { get; private set; } = null!;
	[PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
	[PluginService] internal static IGameGui GameGui { get; private set; } = null!;
	[PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
	[PluginService] internal static ICondition Condition { get; private set; } = null!;
	[PluginService] internal static IDataManager DataManager { get; private set; } = null!;
	public readonly WindowSystem WindowSystem = new("SamplePlugin");
	public static Configuration Configuration { get; private set; } = null!;
	private MainWindow MainWindow { get; init; }

	public Plugin() {
		MainWindow = new MainWindow();
		WindowSystem.AddWindow(MainWindow);
		PluginInterface.UiBuilder.Draw += DrawUI;
		PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
		ECommonsMain.Init(PluginInterface, this);
		Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
		Utils.Init(Log, DataManager,Configuration.UnknownCardConfig);
	}

	public void Dispose() {
		WindowSystem.RemoveAllWindows();
		MainWindow.Dispose();
		ECommonsMain.Dispose();
	}
	private void DrawUI() => WindowSystem.Draw();

	public void ToggleMainUI() => MainWindow.Toggle();
}