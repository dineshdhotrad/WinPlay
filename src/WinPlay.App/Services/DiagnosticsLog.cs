// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;

namespace WinPlay.App.Services;

/// <summary>
/// Bounded, thread-safe rolling log of recent session events for a diagnostics view.
/// Keeps the last <see cref="Capacity"/> entries with timestamps; oldest drop off.
/// </summary>
public sealed class DiagnosticsLog
{
    public const int Capacity = 200;

    public readonly record struct Entry(DateTime TimestampUtc, string Destination, string Message);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public event Action<Entry>? EntryAdded;

    public void Add(string destination, string message)
    {
        var entry = new Entry(DateTime.UtcNow, destination, message);
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _)) { }
        EntryAdded?.Invoke(entry);
    }

    public IReadOnlyList<Entry> Snapshot() => _entries.ToArray();

    public string Export() =>
        string.Join(Environment.NewLine, Snapshot()
            .Select(e => $"{e.TimestampUtc:HH:mm:ss.fff}  [{e.Destination}]  {e.Message}"));
}
