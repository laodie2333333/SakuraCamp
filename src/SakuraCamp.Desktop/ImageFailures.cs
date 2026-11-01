using System.IO;
using System.Xml;

namespace SakuraCamp.Desktop;

internal static class ImageFailures
{
    // InvalidDataException is not derived from IOException. KTEX decoders and ZIP
    // readers use it for unsupported or damaged optional image resources.
    internal static bool IsRecoverable(Exception error) => error is InvalidDataException or IOException
        or UnauthorizedAccessException or XmlException or ArgumentException or FormatException
        or OverflowException or NotSupportedException;
}
