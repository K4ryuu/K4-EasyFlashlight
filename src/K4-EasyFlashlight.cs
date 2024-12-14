using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

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

	[JsonPropertyName("DetectUseButton")]
	public bool DetectUseButton { get; set; } = true;

	[JsonPropertyName("RegisterCommands")]
	public List<string> RegisterCommands { get; set; } = ["flashlight", "fl"];

	[JsonPropertyName("ConfigVersion")]
	public override int Version { get; set; } = 1;
}

[MinimumApiVersion(270)]
public sealed class Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
	public override string ModuleName => "CS2 Easy Flashlight";
	public override string ModuleAuthor => "K4ryuu";
	public override string ModuleDescription => "A simple plugin that allows you to toggle the flashlight with a command or a keybind.";
	public override string ModuleVersion => "1.0.0";

	public required PluginConfig Config { get; set; } = new();
	public void OnConfigParsed(PluginConfig config) => Config = config;

	private readonly Dictionary<int, FlashlightData> _flashlightData = [];
	private Color _cachedColor;
	private static readonly DateTime MinToggleTime = DateTime.MinValue;

	private const float TOGGLE_COOLDOWN = 0.25f;
	private const float DUCK_HEIGHT = 43.50f;
	private const float STAND_HEIGHT = 61.75f;
	private const int DIRECT_LIGHT = 3;

	public override void Load(bool hotReload)
	{
		_cachedColor = ColorTranslator.FromHtml(Config.FlashlightColorRGB);

		RegisterListener<Listeners.OnTick>(OnTick);
		RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath, HookMode.Post);
		RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Post);

		RegisterCommands();
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

		var userId = player!.UserId ?? 0;
		if (userId == 0) return;

		if (!_flashlightData.TryGetValue(userId, out var data))
		{
			data = new FlashlightData();
			_flashlightData[userId] = data;
		}

		ToggleFlashlight(player, data);
	}

	private void OnTick()
	{
		if (!Config.DetectUseButton) return;

		foreach (var player in Utilities.GetPlayers())
		{
			if (!IsValidPlayer(player)) continue;

			var userId = player.UserId ?? 0;
			if (userId == 0) continue;

			if (!_flashlightData.TryGetValue(userId, out var data))
			{
				data = new FlashlightData();
				_flashlightData[userId] = data;
			}

			HandleFlashlightToggle(player, data);
			UpdateFlashlightPosition(player, data);
		}
	}

	private static bool IsValidPlayer(CCSPlayerController? player) =>
		player is { IsValid: true, IsBot: false, UserId: not null };

	private void HandleFlashlightToggle(CCSPlayerController player, FlashlightData data)
	{
		if (!player.Buttons.HasFlag(PlayerButtons.Use))
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
		data.Flashlight.Create(player.Buttons.HasFlag(PlayerButtons.Duck));
	}

	private static void UpdateFlashlightPosition(CCSPlayerController player, FlashlightData data)
	{
		data.Flashlight?.Teleport(player.Buttons.HasFlag(PlayerButtons.Duck));
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
			if (_flashlightData.Remove(player.UserId.Value, out var data))
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
		private COmniLight? _entity;
		private bool _isDisposed;

		public Flashlight(Plugin plugin, CCSPlayerController player, in Color color)
		{
			_plugin = plugin;
			_player = player;
			_color = color;
		}

		public void Create(bool isDucking)
		{
			if (_isDisposed) return;

			_entity = Utilities.CreateEntityByName<COmniLight>("light_omni2");
			if (_entity == null || !_entity.IsValid) return;

			_entity.DirectLight = DIRECT_LIGHT;
			Teleport(isDucking);

			_entity.OuterAngle = _plugin.Config.FlashlightAngle;
			_entity.Enabled = true;
			_entity.Color = _color;
			_entity.ColorTemperature = _plugin.Config.FlashlightColorTemperature;
			_entity.Brightness = _plugin.Config.FlashlightBrightness;
			_entity.Range = _plugin.Config.FlashlightRange;
			_entity.DispatchSpawn();
		}

		public void Teleport(bool isDucking)
		{
			if (_isDisposed || _entity == null || !_entity.IsValid || _player.PlayerPawn.Value?.AbsOrigin == null)
				return;

			var pawn = _player.PlayerPawn.Value;
			_entity.Teleport(
				new Vector(
					pawn.AbsOrigin.X,
					pawn.AbsOrigin.Y,
					pawn.AbsOrigin.Z + (isDucking ? DUCK_HEIGHT : STAND_HEIGHT)
				),
				pawn.EyeAngles,
				pawn.AbsVelocity
			);
		}

		public void Remove()
		{
			if (_isDisposed || _entity == null || !_entity.IsValid)
				return;

			_entity.Remove();
			_entity = null;
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