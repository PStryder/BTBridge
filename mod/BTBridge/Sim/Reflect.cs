using System;
using System.Reflection;

namespace BTBridge.Sim
{
    /// <summary>Access to private members by name (fields first, then properties), walking base types.</summary>
    public static class Reflect
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static object Get(object target, string name)
        {
            if (target == null)
            {
                return null;
            }
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, All | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    return f.GetValue(target);
                }
                var p = t.GetProperty(name, All | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0)
                {
                    return p.GetValue(target, null);
                }
            }
            throw new MissingMemberException(target.GetType().Name, name);
        }

        public static T Get<T>(object target, string name) => (T)Get(target, name);

        public static object Call(object target, string name, params object[] args)
        {
            for (var t = target.GetType(); t != null; t = t.BaseType)
            {
                foreach (var m in t.GetMethods(All | BindingFlags.DeclaredOnly))
                {
                    if (m.Name == name && m.GetParameters().Length == args.Length)
                    {
                        return m.Invoke(target, args);
                    }
                }
            }
            throw new MissingMethodException(target.GetType().Name, name);
        }

        /// <summary>A UI button's click, through its public UnityEvent (what Enter/Escape handlers do).</summary>
        public static void Click(object button)
        {
            var onClicked = Get(button, "OnClicked");
            Call(onClicked, "Invoke");
        }
    }
}
