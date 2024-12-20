using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace CS2EasyFlashlight;

public sealed class PluginConfig : BasePluginConfig
{
	[JsonPropertyName("FlashlightAngle")]
	public float FlashlightAngle { get; set; } = 35f;

	[JsonPropertyName("FlashlightColorRGB")]
	public string FlashlightColorRGB { get; set; } = "#FFFFFF";

	[JsonPropertyName("FlashlightColorTemperature")]
	public int FlashlightColorTemperature { get; set; } = 6500;

	[JsonPropertyName("FlashlightBrightness")]
	public float FlashlightBrightness { get; set; } = 0.75f;

	[JsonPropertyName("FlashlightRange")]
	public float FlashlightRange { get; set; } = 750f;

	[JsonPropertyName("FlashlightDistanceFromBody")]
	public float FlashlightDistanceFromBody { get; set; } = 25f;

	[JsonPropertyName("DetectButtonPress")]
	public bool DetectButtonPress { get; set; } = true;

	[JsonPropertyName("ButtonListener")]
	public string ButtonListener { get; set; } = "Inspect";

	[JsonPropertyName("HideOtherFlashlights")]
	public bool HideOtherFlashlights { get; set; } = true;

	[JsonPropertyName("RegisterCommands")]
	public List<string> RegisterCommands { get; set; } = ["flashlight", "fl"];

	[JsonPropertyName("ConfigVersion")]
	public override int Version { get; set; } = 3;
}

[MinimumApiVersion(270)]
public sealed class Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
	public override string ModuleName => "CS2 Easy Flashlight";
	public override string ModuleAuthor => "K4ryuu";
	public override string ModuleDescription => "A simple plugin that allows you to toggle the flashlight with a command or a keybind.";
	public override string ModuleVersion => "1.0.1";

	public required PluginConfig Config { get; set; } = new();
	private readonly Dictionary<ulong, FlashlightData> _flashlightData = [];
	private Color _cachedColor;
	private static readonly DateTime MinToggleTime = DateTime.MinValue;

	private const float TOGGLE_COOLDOWN = 0.25f;
	private static ulong ACTIVATE_BUTTON = 1UL << 35;

	public void OnConfigParsed(PluginConfig config)
	{
		UpdateConfig(config);
		this.Config = config;

		ACTIVATE_BUTTON = ParseButtonByName(config.ButtonListener);
	}

	private ulong ParseButtonByName(string buttonName)
	{
		switch (buttonName)
		{
			case "Scoreboard":
				return 1UL << 33;
			case "Inspect":
				return 1UL << 35;
		}

		if (Enum.TryParse<PlayerButtons>(buttonName, true, out var button))
		{
			return (ulong)button;
		}

		Logger.LogError($"Warning: Invalid button name '{buttonName}', falling back to default. Available buttons listed in available-buttons.txt");
		return 1UL << 35;
	}

	private void UpdateConfig<T>(T config) where T : BasePluginConfig, new()
	{
		int newVersion = new T().Version;

		if (config.Version == newVersion)
		{
			Logger.LogInformation($"🔄 Configuration is already up-to-date with version {newVersion}. No update required.");
			return;
		}

		Logger.LogInformation($"✨ Updating configuration from version {config.Version} to {newVersion}...");

		config.Version = newVersion;

		string? assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
		if (assemblyName is null)
		{
			Logger.LogError("❌ Failed to update configuration: Assembly name could not be determined.");
			return;
		}

		try
		{
			string configPath = $"{Server.GameDirectory}/csgo/addons/counterstrikesharp/configs/plugins/{assemblyName}/{assemblyName}.json";
			var updatedJsonContent = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
			File.WriteAllText(configPath, updatedJsonContent);

			Logger.LogInformation($"✅ Configuration updated successfully. New version: {newVersion}.");
		}
		catch (Exception ex)
		{
			Logger.LogError($"❌ Failed to update configuration: {ex.Message}");
		}
	}

	public override void Load(bool hotReload)
	{
		_cachedColor = ColorTranslator.FromHtml(Config.FlashlightColorRGB);

		RegisterListener<Listeners.OnTick>(OnTick);
		RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
		RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Post);

		RegisterCommands();

		if (Config.HideOtherFlashlights)
		{
			RegisterListener<Listeners.CheckTransmit>((CCheckTransmitInfoList infoList) =>
			{
				foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
				{
					if (player == null)
						continue;

					foreach (var light in _flashlightData)
					{
						if (light.Key == player.SteamID)
							continue;

						var lightEntity = light.Value.Flashlight?.Entity;
						if (lightEntity?.IsValid == true)
						{
							info.TransmitEntities.Remove(lightEntity);
						}
					}
				}
			});
		}
	}

	private void RegisterCommands()
	{
		foreach (var command in Config.RegisterCommands)
		{
			var cmdName = !command.StartsWith("css_") ? $"css_{command}" : command;
			AddCommand(cmdName, "Toggles the flashlight.", ToggleFlashlightCommand);
		}
	}

	private void ToggleFlashlightCommand(CCSPlayerController? player, CommandInfo info)
	{
		if (!IsValidPlayer(player)) return;

		if (!_flashlightData.TryGetValue(player!.SteamID, out var data))
		{
			data = new FlashlightData();
			_flashlightData[player!.SteamID] = data;
		}

		ToggleFlashlight(player, data);
	}

	private void OnTick()
	{
		foreach (var player in Utilities.GetPlayers())
		{
			if (!IsValidPlayer(player)) continue;

			if (!_flashlightData.TryGetValue(player!.SteamID, out var data))
			{
				data = new FlashlightData();
				_flashlightData[player!.SteamID] = data;
			}

			if (Config.DetectButtonPress)
				HandleFlashlightToggle(player, data);

			UpdateFlashlightPosition(player, data);
		}
	}

	private static bool IsValidPlayer(CCSPlayerController? player) =>
		player is { IsValid: true, IsBot: false, IsHLTV: false } && player.PlayerPawn?.IsValid == true;

	private void HandleFlashlightToggle(CCSPlayerController player, FlashlightData data)
	{
		if (!player.Buttons.HasFlag((PlayerButtons)ACTIVATE_BUTTON))
		{
			data.WasButtonPressed = false;
			return;
		}

		if (data.WasButtonPressed || DateTime.UtcNow.Subtract(data.LastToggleTime).TotalSeconds < TOGGLE_COOLDOWN)
			return;

		ToggleFlashlight(player, data);
		data.LastToggleTime = DateTime.UtcNow;
		data.WasButtonPressed = true;
	}

	private void ToggleFlashlight(CCSPlayerController player, FlashlightData data)
	{
		if (data.Flashlight != null)
		{
			data.Flashlight.Remove();
			data.Flashlight = null;
			return;
		}

		data.Flashlight = new Flashlight(this, player, in _cachedColor);
		data.Flashlight.Create();
	}

	private static void UpdateFlashlightPosition(CCSPlayerController player, FlashlightData data)
	{
		data.Flashlight?.Teleport();
	}

	private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
	{
		CleanupFlashlight(@event.Userid);
		return HookResult.Continue;
	}

	private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
	{
		CleanupFlashlight(@event.Userid);
		return HookResult.Continue;
	}

	public void CleanupFlashlight(CCSPlayerController? player = null)
	{
		if (player?.UserId != null)
		{
			if (_flashlightData.Remove(player!.SteamID, out var data))
			{
				data.Flashlight?.Dispose();
			}
			return;
		}

		foreach (var data in _flashlightData.Values)
		{
			data.Flashlight?.Dispose();
		}
		_flashlightData.Clear();
	}

	public override void Unload(bool hotReload)
	{
		CleanupFlashlight();
	}

	public static Vector GetEyePosition(CCSPlayerController player)
	{
		Vector absorigin = player.PlayerPawn.Value!.AbsOrigin!;
		CPlayer_CameraServices camera = player.PlayerPawn.Value!.CameraServices!;
		return new Vector(absorigin.X, absorigin.Y, absorigin.Z + camera.OldPlayerViewOffsetZ);
	}

	public static Vector GetDirectionOffset(float yawDegrees, float offsetValue)
	{
		float yawRadians = yawDegrees * (float)Math.PI / 180f;
		return new Vector(
			offsetValue * (float)Math.Cos(yawRadians),
			offsetValue * (float)Math.Sin(yawRadians),
			0
		);
	}

	private sealed class FlashlightData
	{
		public Flashlight? Flashlight;
		public DateTime LastToggleTime = MinToggleTime;
		public bool WasButtonPressed;
	}

	private sealed class Flashlight : IDisposable
	{
		private readonly Plugin _plugin;
		private readonly CCSPlayerController _player;
		private readonly Color _color;
		public COmniLight? Entity;
		private bool _isDisposed;

		public Flashlight(Plugin plugin, CCSPlayerController player, in Color color)
		{
			_plugin = plugin;
			_player = player;
			_color = color;
		}

		public void Create()
		{
			if (_isDisposed) return;

			Entity = Utilities.CreateEntityByName<COmniLight>("light_omni2");
			if (Entity == null || !Entity.IsValid) return;

			Entity.DirectLight = 3;
			Teleport();

			Entity.OuterAngle = _plugin.Config.FlashlightAngle;
			Entity.Enabled = true;
			Entity.Color = _color;
			Entity.ColorTemperature = _plugin.Config.FlashlightColorTemperature;
			Entity.Brightness = _plugin.Config.FlashlightBrightness;
			Entity.Range = _plugin.Config.FlashlightRange;
			Entity.DispatchSpawn();
		}

		public void Teleport()
		{
			if (_isDisposed || Entity?.IsValid != true || _player.PlayerPawn.Value == null)
				return;

			var playerPawn = _player.PlayerPawn.Value!;
			var basePos = GetEyePosition(_player);

			var pos = _plugin.Config.FlashlightDistanceFromBody > 0
				? basePos + GetDirectionOffset(playerPawn.AbsRotation!.Y, _plugin.Config.FlashlightDistanceFromBody)
				: basePos;

			Entity.Teleport(pos, playerPawn.EyeAngles!);
		}

		public void Remove()
		{
			if (_isDisposed || Entity == null)
				return;

			if (Entity.IsValid)
				Entity.Remove();

			Entity = null;
		}

		public void Dispose()
		{
			if (_isDisposed) return;

			Remove();
			_isDisposed = true;
			GC.SuppressFinalize(this);
		}
	}
}