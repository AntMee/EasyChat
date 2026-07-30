using System;

namespace EasyChat.Services.Abstractions;

/// <summary>
/// An opaque clipboard snapshot owned by an <see cref="IClipboardSnapshotService"/>.
/// The concrete representation is platform-specific and must not leak into callers.
/// </summary>
public interface IClipboardSnapshot : IDisposable
{
}
