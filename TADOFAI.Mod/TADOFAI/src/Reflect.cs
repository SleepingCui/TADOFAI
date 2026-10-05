using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace TADOFAI.Mod
{
    /// <summary>
    /// 带缓存的跨版本反射读取器。
    ///
    /// 为什么需要它：本 Mod 编译期只引用一个游戏程序集（默认 libs/Assembly-CSharp340.dll），
    /// 但运行期可能落在缺少某些成员的版本上。凡是为了「网页端复刻」而新加的上报字段，
    /// 一律经这里读取：读不到就返回 fallback —— 少一个成员只损失一项上报，
    /// 不会让整帧快照构造抛异常、也不会影响老版本玩家。
    ///
    /// 约定：所有公开方法都不抛异常。
    /// </summary>
    public static class Reflect
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, MemberInfo> Members = new Dictionary<string, MemberInfo>(256);
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>(64);
        private static readonly HashSet<string> Misses = new HashSet<string>();
        private static readonly HashSet<string> Warned = new HashSet<string>();

        private const BindingFlags DeclaredFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        #region 查找

        private static MemberInfo FindMember(Type type, string name, bool isStatic)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;

            string key = Key(type, name, isStatic);
            lock (Gate)
            {
                MemberInfo hit;
                if (Members.TryGetValue(key, out hit)) return hit;
                if (Misses.Contains(key)) return null;
            }

            BindingFlags flags = DeclaredFlags | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            MemberInfo found = null;
            for (Type t = type; t != null && found == null; t = t.BaseType)
            {
                try { found = t.GetField(name, flags); }
                catch { found = null; }
                if (found != null) break;
                try { found = t.GetProperty(name, flags); }
                catch { found = null; }
            }

            lock (Gate)
            {
                if (found != null)
                {
                    Members[key] = found;
                    Misses.Remove(key);
                }
                else
                {
                    Misses.Add(key);
                }
            }
            return found;
        }

        private static MethodInfo FindMethod(Type type, string name, int argCount, bool isStatic)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;

            string key = Key(type, "#" + name + "/" + argCount, isStatic);
            lock (Gate)
            {
                MethodInfo hit;
                if (Methods.TryGetValue(key, out hit)) return hit;
                if (Misses.Contains(key)) return null;
            }

            BindingFlags flags = DeclaredFlags | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
            MethodInfo found = null;
            for (Type t = type; t != null && found == null; t = t.BaseType)
            {
                MethodInfo[] candidates;
                try { candidates = t.GetMethods(flags); }
                catch { candidates = null; }
                if (candidates == null) continue;
                for (int i = 0; i < candidates.Length; i++)
                {
                    MethodInfo m = candidates[i];
                    if (m.Name != name) continue;
                    if (m.GetParameters().Length != argCount) continue;
                    found = m;
                    break;
                }
            }

            lock (Gate)
            {
                if (found != null)
                {
                    Methods[key] = found;
                    Misses.Remove(key);
                }
                else
                {
                    Misses.Add(key);
                }
            }
            return found;
        }

        private static string Key(Type type, string name, bool isStatic)
        {
            return (type.FullName ?? type.Name) + "|" + name + (isStatic ? "|s" : "|i");
        }

        #endregion

        #region 读取

        private static object Read(MemberInfo member, object target)
        {
            if (member == null) return null;
            try
            {
                FieldInfo field = member as FieldInfo;
                if (field != null) return field.GetValue(field.IsStatic ? null : target);

                PropertyInfo property = member as PropertyInfo;
                if (property == null || !property.CanRead) return null;
                MethodInfo getter = property.GetGetMethod(true);
                if (getter == null) return null;
                return property.GetValue(getter.IsStatic ? null : target, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取实例成员（字段或属性，含基类）。失败返回 null。</summary>
        public static object Get(object target, string memberName)
        {
            if (target == null) return null;
            return Read(FindMember(target.GetType(), memberName, false), target);
        }

        /// <summary>读取静态成员。失败返回 null。</summary>
        public static object GetStatic(Type type, string memberName)
        {
            return Read(FindMember(type, memberName, true), null);
        }

        public static Array GetArray(object target, string memberName)
        {
            return Get(target, memberName) as Array;
        }

        public static Array GetStaticArray(Type type, string memberName)
        {
            return GetStatic(type, memberName) as Array;
        }

        #endregion

        #region 调用

        public static object Call(object target, string methodName, params object[] args)
        {
            if (target == null) return null;
            return Invoke(target, target.GetType(), methodName, args, false);
        }

        public static object CallStatic(Type type, string methodName, params object[] args)
        {
            return Invoke(null, type, methodName, args, true);
        }

        private static object Invoke(object target, Type type, string methodName, object[] args, bool isStatic)
        {
            int count = args == null ? 0 : args.Length;
            MethodInfo method = FindMethod(type, methodName, count, isStatic);
            if (method == null) return null;
            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                object[] coerced = new object[count];
                for (int i = 0; i < count; i++) coerced[i] = Coerce(parameters[i].ParameterType, args[i]);
                return method.Invoke(method.IsStatic ? null : target, coerced);
            }
            catch
            {
                return null;
            }
        }

        private static object Coerce(Type want, object value)
        {
            if (value == null) return null;
            if (want == null || want.IsInstanceOfType(value)) return value;

            Type underlying = Nullable.GetUnderlyingType(want);
            if (underlying != null) return Coerce(underlying, value);

            if (want.IsEnum)
            {
                try
                {
                    if (value is string) return Enum.Parse(want, (string)value, true);
                    return Enum.ToObject(want, value);
                }
                catch
                {
                    return value;
                }
            }

            try { return Convert.ChangeType(value, want, CultureInfo.InvariantCulture); }
            catch { return value; }
        }

        #endregion

        #region 类型转换

        public static int ToInt(object value, int fallback)
        {
            if (value == null) return fallback;
            if (value is int) return (int)value;
            try { return Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static long ToLong(object value, long fallback)
        {
            if (value == null) return fallback;
            if (value is long) return (long)value;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static float ToFloat(object value, float fallback)
        {
            if (value == null) return fallback;
            if (value is float)
            {
                float f = (float)value;
                return NumUtil.IsFinite(f) ? f : fallback;
            }
            if (value is bool) return ((bool)value) ? 1f : 0f;
            try
            {
                if (value is string)
                {
                    float parsed;
                    if (!float.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        return fallback;
                    return NumUtil.IsFinite(parsed) ? parsed : fallback;
                }
                float converted = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return NumUtil.IsFinite(converted) ? converted : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static double ToDouble(object value, double fallback)
        {
            if (value == null) return fallback;
            if (value is double)
            {
                double d = (double)value;
                return NumUtil.IsFinite(d) ? d : fallback;
            }
            try
            {
                double converted = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return NumUtil.IsFinite(converted) ? converted : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        public static bool ToBool(object value, bool fallback)
        {
            if (value is bool) return (bool)value;
            return fallback;
        }

        public static string ToStr(object value, string fallback)
        {
            if (value == null) return fallback;
            try
            {
                string text = value as string;
                if (text != null) return text;
                object nested = Get(value, "name");
                string name = nested as string;
                if (!string.IsNullOrEmpty(name)) return name;
                return value.ToString();
            }
            catch
            {
                return fallback;
            }
        }

        #endregion

        #region 便捷读取

        public static int GetInt(object target, string memberName, int fallback)
        {
            return ToInt(Get(target, memberName), fallback);
        }

        public static float GetFloat(object target, string memberName, float fallback)
        {
            return ToFloat(Get(target, memberName), fallback);
        }

        public static double GetDouble(object target, string memberName, double fallback)
        {
            return ToDouble(Get(target, memberName), fallback);
        }

        public static bool GetBool(object target, string memberName, bool fallback)
        {
            return ToBool(Get(target, memberName), fallback);
        }

        public static string GetString(object target, string memberName, string fallback)
        {
            return ToStr(Get(target, memberName), fallback);
        }

        public static int GetStaticInt(Type type, string memberName, int fallback)
        {
            return ToInt(GetStatic(type, memberName), fallback);
        }

        public static float GetStaticFloat(Type type, string memberName, float fallback)
        {
            return ToFloat(GetStatic(type, memberName), fallback);
        }

        public static double GetStaticDouble(Type type, string memberName, double fallback)
        {
            return ToDouble(GetStatic(type, memberName), fallback);
        }

        public static bool GetStaticBool(Type type, string memberName, bool fallback)
        {
            return ToBool(GetStatic(type, memberName), fallback);
        }

        public static string GetStaticString(Type type, string memberName, string fallback)
        {
            return ToStr(GetStatic(type, memberName), fallback);
        }

        #endregion

        #region 诊断

        /// <summary>同一个 key 只告警一次，避免每帧刷屏。</summary>
        public static void WarnOnce(string key, string message)
        {
            lock (Gate)
            {
                if (!Warned.Add(key)) return;
            }
            ModLog.Warn(message);
        }

        public static void Reset()
        {
            lock (Gate)
            {
                Members.Clear();
                Methods.Clear();
                Misses.Clear();
            }
        }

        #endregion
    }
}
