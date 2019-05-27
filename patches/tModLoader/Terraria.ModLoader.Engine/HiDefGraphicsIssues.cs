using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Terraria.ModLoader.Engine
{
	/// <summary>
	/// XNA makes a distinction between graphics device resets and creations.
	/// Calls to <code>GraphicsDevice.Reset</code> via <code>GraphicsDeviceManager.ApplyChanges</code> trigger a device reset on screen resolution and other changes.
	/// When a device is reset, all textures are re-uploaded from native copies stored in RAM, and the game continues to run with valid handles.
	/// When changing graphics profile, the device must be re-created, and all graphics resources must be re-initialized manually.
	/// 
	/// Sometimes (we haven't been able to track the actual cause) seemingly innocuous resets with HiDef graphics profile, fail in XNA native code, and GraphicsDevice.Reset falls back to a full recreation.
	/// XNA includes this fallback try-catch path, implying that this is a known or expected issue, however tML (and Terraria) are 
	/// not equipped to handle re-acquiring all graphics resources at any point in the load cycle.
	/// 
	/// This failed reset manifests as a silently caught InvalidOperationException, and then later causes an engine crash, normally via an ObjectDisposedException.
	/// The graphics device does not fail the initial switch to HiDef, and for most users, only fails some portion of the time (a heisenbug).
	/// 
	/// The HiDef graphics profile is normally only enabled with the "Support4K" configuration flag, and on monitors with display resolutions above 1080p
	/// However, due to some advanced shader features available on HiDef, there has been request for tML to enable HiDef graphics by default where possible.
	/// </summary>
	internal static class HiDefGraphicsIssues
	{
		private static int mainThreadId;

		public static void Init()
        {
#if XNA
            new Hook(typeof(GraphicsDeviceManager).FindMethod("CanResetDevice"), new hook_CanResetDevice(HookCanResetDevice));
            
			// attempt to stealthily allow high-res textures on Reach (XNA does this internally when loading from pngs)
			var t_ProfileCapabilities = typeof(Texture).Assembly.GetType("Microsoft.Xna.Framework.Graphics.ProfileCapabilities");
			var f_ProfileCapabilities_Reach = t_ProfileCapabilities.GetField("Reach", BindingFlags.NonPublic | BindingFlags.Static);
			var f_MaxTextureSize = t_ProfileCapabilities.GetField("MaxTextureSize", BindingFlags.NonPublic | BindingFlags.Instance);
			f_MaxTextureSize.SetValue(f_ProfileCapabilities_Reach.GetValue(null), 4096);
#endif
#if DEBUG
	        mainThreadId = Thread.CurrentThread.ManagedThreadId;
	        new Hook(typeof(Texture2D).FindMethod("ValidateCreationParameters"), new hook_ValidateCreationParameters(HookValidateCreationParameters));
#endif
        }

		private delegate bool orig_CanResetDevice(GraphicsDeviceManager self, GraphicsDeviceInformation newDeviceInfo);
		private delegate bool hook_CanResetDevice(orig_CanResetDevice orig, GraphicsDeviceManager self, GraphicsDeviceInformation newDeviceInfo);
		private static bool HookCanResetDevice(orig_CanResetDevice orig, GraphicsDeviceManager self, GraphicsDeviceInformation newDeviceInfo)
		{
			LogGraphicsDevice();
			
			var changes = new StringBuilder();
			void AddParam<T>(string name, T t1, T t2)
			{
				changes.Append(", ").Append(name).Append(": ").Append(t1);
				if (!EqualityComparer<T>.Default.Equals(t1, t2))
					changes.Append(" -> ").Append(t2);
			}
			
			AddParam("Profile", self.GraphicsDevice.GraphicsProfile, newDeviceInfo.GraphicsProfile);
			AddParam("Width", self.GraphicsDevice.PresentationParameters.BackBufferWidth, newDeviceInfo.PresentationParameters.BackBufferWidth);
			AddParam("Height", self.GraphicsDevice.PresentationParameters.BackBufferHeight, newDeviceInfo.PresentationParameters.BackBufferHeight);
			AddParam("Fullscreen", self.GraphicsDevice.PresentationParameters.IsFullScreen, newDeviceInfo.PresentationParameters.IsFullScreen);
			AddParam("Display", self.GraphicsDevice.Adapter.DeviceName, newDeviceInfo.Adapter.DeviceName);
			
			Logging.Terraria.Debug("Device Reset"+changes);
			
			// we can force a device recreation, to 'simulate' the bug as it's not very reproducible
			//return false;

			return orig(self, newDeviceInfo);
		}
			    
		private delegate void orig_ValidateCreationParameters(object profile, int width, int height, SurfaceFormat format, [MarshalAs(UnmanagedType.U1)] bool mipMap);
		private delegate void hook_ValidateCreationParameters(orig_ValidateCreationParameters orig, object profile, int width, int height, SurfaceFormat format, [MarshalAs(UnmanagedType.U1)] bool mipMap);
		private static void HookValidateCreationParameters(orig_ValidateCreationParameters orig, object profile, int width, int height, SurfaceFormat format, [MarshalAs(UnmanagedType.U1)] bool mipMap) {
			if (!Program.LoadedEverything && Thread.CurrentThread.ManagedThreadId != mainThreadId)
				throw new Exception("Texture created on worker thread before graphics device is finalised");
			
			orig(profile, width, height, format, mipMap);
		}

		/// <summary>
		/// Main.ContentLoad is called every time the device is recreated. Some small modifications have been made to allow Terraria to recover (re-acquire textures)
		/// if the device is recreated early enough, but once JIT finishes and tML loading begins further recreations cannot be silently handled, and will require disabling HiDef graphics.
		/// </summary>
		public static void OnLoadContent()
		{
			LogGraphicsDevice();
#if XNA
			if (Program.LoadedEverything)
				ReportFatalEngineReload();
#endif
		}

		private static void ReportFatalEngineReload()
		{
			Logging.tML.Fatal("Graphics device reset after main engine load");
			Main.Support4K = false;
			Main.SaveSettings();
			Logging.tML.Debug("Disabled Main.Support4K");

			// TODO: upload log to server for analysis
			
			//var modsAffected = ModContent.HiDefMods.Count == 0 ? "No mods will be affected." : $"The following mods will be affected {string.Join(", ", ModContent.HiDefMods.Select(m => m.DisplayName))}";
			MessageBox.Show("tML encountered a crash when testing some experimental graphics features. Experimental features will be disabled. \nPlease restart your game.", "Graphics Engine Failure", MessageBoxButtons.OK, MessageBoxIcon.Error);
			Environment.Exit(1);
		}

		private static int CurrentDevice = -1;
		private static void LogGraphicsDevice()
		{
			var adapter = Main.graphics.GraphicsDevice.Adapter;
			if (CurrentDevice == adapter.DeviceId)
				return;

			CurrentDevice = adapter.DeviceId;
			Logging.Terraria.Debug($"Graphics Device: {adapter.Description} {adapter.CurrentDisplayMode}");
		}
    }
}