using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace TADOFAI.Mod
{
    /// <summary>
    /// Patch 注册、启用、卸载。
    ///
    /// 约定：Register 传入的是「容器类型」，容器自身或它的嵌套类型只要带 [HarmonyPatch]
    /// 就会被处理，所以一组相关 Patch 可以放在同一个静态类里。
    ///
    /// 卸载只使用自己的 Harmony ID，绝不调用全局 UnpatchAll()。
    /// </summary>
    public static class PatchManager
    {
        private static readonly List<Registration> Registrations = new List<Registration>();
        private static readonly List<Type> AppliedPatchClasses = new List<Type>();

        private sealed class Registration
        {
            public readonly Type Container;
            public readonly Func<bool> Gate;

            public Registration(Type container, Func<bool> gate)
            {
                Container = container;
                Gate = gate;
            }
        }

        /// <summary>
        /// 注册一组 Patch。gate 只在注册时求值一次。
        ///
        /// 配置界面（SettingsUI）里的开关都属于「运行中会变」的开关，必须无条件挂载、
        /// 在 Patch 方法内部读 Settings.Current 判断；用 gate 会让改动要重启才生效。
        /// gate 只适合启动时就能定下来、且挂载代价很高的 Patch。
        /// </summary>
        public static void Register(Type containerType, Func<bool> gate = null)
        {
            if (containerType == null) return;
            Registrations.Add(new Registration(containerType, gate));
        }

        public static void ApplyAll()
        {
            if (Main.Harmony == null)
            {
                ModLog.Error("PatchManager: Harmony 未初始化，跳过 Patch");
                return;
            }

            for (int i = 0; i < Registrations.Count; i++)
            {
                Registration registration = Registrations[i];

                if (registration.Gate != null && !registration.Gate())
                {
                    ModLog.Info("Patch: " + registration.Container.Name + " SKIP（开关关闭）");
                    continue;
                }

                PatchContainer(registration.Container);
            }

            ReportApplied();
        }

        public static void UnpatchAll()
        {
            if (Main.Harmony == null)
            {
                AppliedPatchClasses.Clear();
                Registrations.Clear();
                return;
            }

            for (int i = AppliedPatchClasses.Count - 1; i >= 0; i--)
            {
                Type patchClass = AppliedPatchClasses[i];
                try
                {
                    Main.Harmony.CreateClassProcessor(patchClass).Unpatch();
                }
                catch (Exception ex)
                {
                    ModLog.Warn("Unpatch: " + patchClass.Name + " 失败: " + ex.Message);
                }
            }

            AppliedPatchClasses.Clear();
            Registrations.Clear();

            try
            {
                // 只解除本 Harmony ID 的补丁，避免影响其他 Mod
                Main.Harmony.UnpatchAll(Main.HarmonyId);
            }
            catch (Exception ex)
            {
                ModLog.Warn("UnpatchAll 失败: " + ex.Message);
            }
        }

        private static void PatchContainer(Type container)
        {
            foreach (Type patchClass in EnumeratePatchClasses(container))
            {
                if (!IsPatchClass(patchClass)) continue;

                try
                {
                    Main.Harmony.CreateClassProcessor(patchClass).Patch();
                    AppliedPatchClasses.Add(patchClass);
                }
                catch (Exception ex)
                {
                    // 单个 Patch 失败只禁用该功能，不让整个 Mod 退出
                    ModLog.Warn("Patch: " + patchClass.Name + " FAILED（已跳过该功能）: " + ex.Message);
                }
            }
        }

        /// <summary>把容器自身和它的嵌套类型都算作候选 Patch 类。</summary>
        private static IEnumerable<Type> EnumeratePatchClasses(Type type)
        {
            yield return type;

            Type[] nested;
            try
            {
                nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch
            {
                nested = new Type[0];
            }

            for (int i = 0; i < nested.Length; i++)
            {
                foreach (Type inner in EnumeratePatchClasses(nested[i]))
                    yield return inner;
            }
        }

        private static bool IsPatchClass(Type type)
        {
            try
            {
                return type.GetCustomAttributes(typeof(HarmonyPatch), true).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void ReportApplied()
        {
            try
            {
                foreach (MethodBase method in Main.Harmony.GetPatchedMethods())
                {
                    string declaring = method.DeclaringType != null ? method.DeclaringType.Name : "?";
                    ModLog.Info("Patch: " + declaring + "." + method.Name + "  OK");
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn("读取 Patch 列表失败: " + ex.Message);
            }
        }
    }
}
