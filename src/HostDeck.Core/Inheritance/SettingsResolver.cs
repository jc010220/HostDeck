using System.Collections.Concurrent;
using System.Reflection;
using HostDeck.Core.Model;

namespace HostDeck.Core.Inheritance;

/// <summary>
/// Löst die effektiven Einstellungen eines Knotens auf.
///
/// Algorithmus (pro Feld, auch innerhalb verschachtelter Gruppen):
/// <code>
/// effektiv = Knoten.Feld ?? Eltern.Feld ?? Großeltern.Feld ?? … ?? Wurzel.Feld ?? Defaults.Feld
/// </code>
/// Danach: Port ohne Wert ⇒ Standardport des effektiven Protokolls.
/// </summary>
public static class SettingsResolver
{
    private sealed record PropMeta(PropertyInfo Property, bool IsGroup);

    private static readonly ConcurrentDictionary<Type, PropMeta[]> MetaCache = new();

    /// <summary>Liefert vollständig befüllte, effektive Settings (neues Objekt, Eingaben bleiben unverändert).</summary>
    public static ConnectionSettings Resolve(TreeNode node, ConnectionSettings? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var chain = Chain(node).Select(n => (object)n.Settings).Append(defaults ?? ConnectionSettings.Defaults).ToArray();
        var result = new ConnectionSettings();
        MergeInto(result, chain);
        result.Port ??= ProtocolDefaults.DefaultPort(result.Protocol ?? ProtocolKind.Rdp);
        return result;
    }

    /// <summary>
    /// Woher stammt der effektive Wert? Liefert den Knoten, an dem das Feld explizit gesetzt ist,
    /// oder <c>null</c>, wenn der eingebaute Default greift. Für UI-Hinweise wie "geerbt von: Ordner X".
    /// </summary>
    public static TreeNode? FindOrigin<T>(TreeNode node, Func<ConnectionSettings, T?> selector)
        => Chain(node).FirstOrDefault(n => selector(n.Settings) is not null);

    /// <summary>True, wenn das Feld an diesem Knoten nicht explizit gesetzt ist.</summary>
    public static bool IsInherited<T>(TreeNode node, Func<ConnectionSettings, T?> selector)
        => selector(node.Settings) is null;

    /// <summary>Setzt alle Felder (rekursiv) auf null, d.h. "alles erben" – entspricht mRemoteNG "Inherit everything".</summary>
    public static void InheritEverything(ConnectionSettings settings) => ClearRecursive(settings);

    private static IEnumerable<TreeNode> Chain(TreeNode node)
    {
        yield return node;
        foreach (var a in node.Ancestors())
            yield return a;
    }

    private static PropMeta[] Meta(Type t) => MetaCache.GetOrAdd(t, static type =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p is { CanRead: true, CanWrite: true })
            .Select(p => new PropMeta(p, p.PropertyType.GetCustomAttribute<SettingsGroupAttribute>() is not null))
            .ToArray());

    private static void MergeInto(object target, object[] chain)
    {
        foreach (var meta in Meta(target.GetType()))
        {
            if (meta.IsGroup)
            {
                var subTarget = Activator.CreateInstance(meta.Property.PropertyType)!;
                var subChain = chain.Select(c => meta.Property.GetValue(c)).Where(v => v is not null).ToArray();
                MergeInto(subTarget!, subChain!);
                meta.Property.SetValue(target, subTarget);
                continue;
            }

            foreach (var source in chain)
            {
                var value = meta.Property.GetValue(source);
                if (value is null) continue;
                meta.Property.SetValue(target, value);
                break;
            }
        }
    }

    private static void ClearRecursive(object settings)
    {
        foreach (var meta in Meta(settings.GetType()))
        {
            if (meta.IsGroup)
            {
                var sub = meta.Property.GetValue(settings);
                if (sub is not null) ClearRecursive(sub);
            }
            else
            {
                meta.Property.SetValue(settings, null);
            }
        }
    }
}
