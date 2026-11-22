using Microsoft.Win32;

namespace PixelThumbs;

internal sealed class RegistryTransaction
{
    private readonly Stack<Action> undo = new();

    internal static Value? Read(RegistryKey root, string path, string name = "")
    {
        using var key = root.OpenSubKey(path);
        return ReadValue(key, name);
    }

    internal bool Write(RegistryKey root, string path, string name, Value? value)
    {
        using var key = value is null ? root.OpenSubKey(path, writable: true) : EnsureKey(root, path);
        var previous = ReadValue(key, name);
        if (Equals(previous, value))
            return false;

        undo.Push(() =>
        {
            using var current = root.OpenSubKey(path, writable: true);
            // Avoid undoing an unrelated change made by another application.
            if (current is not null && Equals(ReadValue(current, name), value))
                SetValue(current, name, previous);
        });
        SetValue(key!, name, value);
        return true;
    }

    internal void Prune(RegistryKey root, string path, string boundary)
    {
        while (!path.Equals(boundary, StringComparison.OrdinalIgnoreCase) && IsEmpty(root, path))
        {
            string removed = path;
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
            undo.Push(() => { using var restored = root.CreateSubKey(removed); });
            path = Path.GetDirectoryName(path)!;
        }
    }

    internal void Run(Action changes)
    {
        try
        {
            changes();
            undo.Clear();
        }
        catch (Exception failure)
        {
            List<Exception> rollbackFailures = [];
            while (undo.TryPop(out var rollback))
            {
                try { rollback(); }
                catch (Exception error) { rollbackFailures.Add(error); }
            }
            if (rollbackFailures.Count != 0)
                throw new InvalidOperationException("Не удалось полностью восстановить записи реестра: " +
                    rollbackFailures[0].Message, new AggregateException([failure, .. rollbackFailures]));
            throw;
        }
    }

    private RegistryKey EnsureKey(RegistryKey root, string path)
    {
        if (root.OpenSubKey(path, writable: true) is { } existing)
            return existing;

        string missing = path;
        while (Path.GetDirectoryName(missing) is { Length: > 0 } parent)
        {
            using var key = root.OpenSubKey(parent);
            if (key is not null)
                break;
            missing = parent;
        }

        TrackCreatedKeys(root, path, missing);
        return root.CreateSubKey(path);
    }

    private void TrackCreatedKeys(RegistryKey root, string path, string firstMissing)
    {
        var created = new Stack<string>();
        for (string current = path;; current = Path.GetDirectoryName(current)!)
        {
            created.Push(current);
            if (current.Equals(firstMissing, StringComparison.OrdinalIgnoreCase))
                break;
        }
        foreach (string key in created)
            undo.Push(() =>
            {
                if (IsEmpty(root, key))
                    root.DeleteSubKey(key, throwOnMissingSubKey: false);
            });
    }

    private static bool IsEmpty(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is { ValueCount: 0, SubKeyCount: 0 };
    }

    private static Value? ReadValue(RegistryKey? key, string name)
    {
        object? data = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return data is null ? null : new Value(data, key!.GetValueKind(name));
    }

    private static void SetValue(RegistryKey key, string name, Value? value)
    {
        if (value is { } entry)
            key.SetValue(name, entry.Data, entry.Kind);
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    internal sealed record Value(object Data, RegistryValueKind Kind = RegistryValueKind.String)
    {
        public bool Equals(Value? other) => other is not null && Kind == other.Kind &&
            System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(Data, other.Data);

        public override int GetHashCode() => HashCode.Combine(Kind,
            System.Collections.StructuralComparisons.StructuralEqualityComparer.GetHashCode(Data));
    }
}
