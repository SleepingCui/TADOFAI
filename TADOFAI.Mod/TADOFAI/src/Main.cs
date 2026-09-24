using System;
using HarmonyLib;
using TADOFAI.Mod.Loader;
using TADOFAI.Mod.Patches;

namespace TADOFAI.Mod
{
    /// <summary>
    /// 共享入口。加载器只负责适配，启用 / 禁用逻辑都在这里。
    /// </summary>
    public static class Main
    {
        public const string HarmonyId = "TADOFAI.Mod";
        public const string ModVersion = "0.1.0";

        private static double _lastStatePublish;

        public static Harmony Harmony { get; private set; }

        public static IModLoader Loader { get; private set; }

        public static bool IsEnabled { get; private set; }

        public static void Bind(IModLoader loader)
        {
            if (loader == null) return;

            Loader = loader;
            loader.OnToggle += SetEnabled;
            loader.OnUpdate += Update;
        }

        public static void SetEnabled(bool value)
        {
            if (value) Enable();
            else Disable();
        }

        public static void Enable()
        {
            if (IsEnabled) return;

            try
            {
                ModLog.Info("TADOFAI.Mod " + ModVersion + " 正在启用");

                Harmony = new Harmony(HarmonyId);
                GameRefs.Bind();
                VersionSafe.Setup();
                RegisterPatches();
                PatchManager.ApplyAll();
                Transport.Start();

                IsEnabled = true;
                ModLog.Info("TADOFAI.Mod 启用完成");
            }
            catch (Exception ex)
            {
                ModLog.Error("启用失败: " + ex);
                Shutdown();
            }
        }

        public static void Disable()
        {
            if (!IsEnabled && Harmony == null) return;

            Shutdown();
            Settings.Save();

            ModLog.Info("TADOFAI.Mod 已禁用");
        }

        public static void Update(float deltaTime)
        {
            if (!IsEnabled) return;

            try
            {
                Collector.Sample();

                // 状态按 StateRate 合并发布，避免每帧创建快照
                double now = Transport.NowSeconds;
                double interval = 1.0 / Math.Max(1.0, Settings.Current.StateRate);
                if (now - _lastStatePublish < interval) return;

                _lastStatePublish = now;
                Transport.PublishState(Collector.BuildSnapshotMessage());
            }
            catch (Exception ex)
            {
                ModLog.WarnOnce("Main.Update", "Update 异常: " + ex.Message);
            }
        }

        private static void RegisterPatches()
        {
            // 生命周期与判定路径
            PatchManager.Register(typeof(GameLifecyclePatches));
            PatchManager.Register(typeof(PlayPatches));

            // Timing Patch 始终挂载，开关在方法内部快速判断
            PatchManager.Register(typeof(TimingPatches));

            // 下面这些也全部无条件挂载：设置界面可以在运行中改开关，
            // 若用 Register 的 gate 在启动时决定是否挂载，改了开关就得重启才生效。
            PatchManager.Register(typeof(DebugPatches));

            if (VersionSafe.IsV141OrLater)
            {
                PatchManager.Register(typeof(V141Patches));
                PatchManager.Register(typeof(V141AccuracyPatch));
            }
            else
            {
                PatchManager.Register(typeof(V136Patches));
                PatchManager.Register(typeof(V136AccuracyPatch));
            }
        }

        private static void Shutdown()
        {
            IsEnabled = false;
            Harmony = null;

            try
            {
                Transport.Stop();
            }
            catch (Exception ex)
            {
                ModLog.Warn("停止 Transport 失败: " + ex.Message);
            }

            try
            {
                PatchManager.UnpatchAll();
            }
            catch (Exception ex)
            {
                ModLog.Warn("卸载 Patch 失败: " + ex.Message);
            }

            ModLog.ResetOnceKeys();
        }
    }
}
