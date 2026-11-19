using System.Runtime.InteropServices;

namespace GlyphLens.Windows.Tests.Integration;

/// <summary>
/// Counts this process's kernel handles of one object type (for example "Section"), which is far less noisy
/// than <c>Process.HandleCount</c> (thread-pool threads and events come and go).
/// </summary>
internal static unsafe partial class ProcessHandleCounter
{
    private const int SystemExtendedHandleInformation = 0x40;
    private const int ObjectTypeInformation = 2;
    private const int ExtendedEntrySize = 40;

    internal static int Count(string typeName)
    {
        var handles = Snapshot();
        var typeIndexByName = new Dictionary<ushort, string>();
        var count = 0;
        foreach (var (handle, typeIndex) in handles)
        {
            if (!typeIndexByName.TryGetValue(typeIndex, out var name))
            {
                name = TypeNameOf((nint)handle);
                typeIndexByName[typeIndex] = name;
            }

            if (string.Equals(name, typeName, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static List<(ulong Handle, ushort TypeIndex)> Snapshot()
    {
        var size = 1 << 22;
        while (true)
        {
            var buffer = new byte[size];
            fixed (byte* p = buffer)
            {
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, p, buffer.Length, out var needed);
                if (status == 0)
                {
                    var result = new List<(ulong, ushort)>();
                    var pid = (ulong)Environment.ProcessId;
                    var total = *(long*)p;
                    var entry = p + 16;
                    for (var i = 0; i < total; i++, entry += ExtendedEntrySize)
                    {
                        if (*(ulong*)(entry + 8) == pid)
                        {
                            result.Add((*(ulong*)(entry + 16), *(ushort*)(entry + 28)));
                        }
                    }

                    return result;
                }

                size = needed > size ? needed + (1 << 20) : size * 2;
            }
        }
    }

    private static string TypeNameOf(nint handle)
    {
        var buffer = stackalloc byte[512];
        if (NtQueryObject(handle, ObjectTypeInformation, buffer, 512, out _) != 0 || *(ushort*)buffer == 0)
        {
            return string.Empty;
        }

        return new string(*(char**)(buffer + 8), 0, *(ushort*)buffer / 2);
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, void* buffer, int length, out int returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(nint handle, int informationClass, void* buffer, int length, out int returnLength);
}
