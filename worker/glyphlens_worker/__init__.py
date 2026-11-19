"""GlyphLens local inference worker.

Hosts (in future phases) OCR, language identification and offline translation for the
GlyphLens desktop application. See ``docs/ARCHITECTURE.md`` and ``docs/IPC-PROTOCOL.md``.
"""

__version__ = "0.1.0"

#: IPC protocol version implemented by this worker (MAJOR.MINOR). Must match the .NET
#: ``GlyphLens.Core.Contracts.ProtocolVersion.Current`` major version.
PROTOCOL_VERSION = "1.0"

__all__ = ["PROTOCOL_VERSION", "__version__"]
