using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace XIVLauncher.Core.Support;

internal static class FfxivConfigBackup
{
    // Version 2 is the cross-platform format produced by Square Enix's
    // FFXIV Configuration Backup Tool. Integers are little-endian, file
    // payloads use zlib, and the checksum is the wrapping sum of bytes
    // following the 16-byte header.
    public const string OfficialFileName = "FFXIVconfig.fea";

    private static readonly byte[] Magic = [0xFF, 0x14, 0x0F, 0xEA];
    private static readonly string[] RootFileNames = ["FFXIV_BOOT.cfg", "MACROSYS.dat"];

    private const ushort FormatVersion = 2;
    private const int HeaderSize = 16;
    private const int MaxCharacterCount = 64;
    private const int MaxFilesPerCharacter = 128;
    private const int MaxFileNameBytes = 520;
    private const int MaxArchiveSize = 512 * 1024 * 1024;
    private const int MaxCompressedFileSize = 64 * 1024 * 1024;
    private const int MaxUncompressedFileSize = 64 * 1024 * 1024;

    public static ExportResult Export(DirectoryInfo configDirectory, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var archive = BuildArchive(configDirectory);
        var finalPath = EnsureFeaExtension(Path.GetFullPath(outputPath));
        var outputDirectory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidDataException("The selected backup path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, archive.Data);
            File.Move(temporaryPath, finalPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new ExportResult(
            finalPath,
            archive.CharacterCount,
            archive.FileCount,
            archive.Data.Length);
    }

    public static ImportResult Import(
        DirectoryInfo configDirectory,
        string inputPath,
        DirectoryInfo safetyBackupDirectory,
        bool preserveNewerFiles)
    {
        ArgumentNullException.ThrowIfNull(configDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(safetyBackupDirectory);

        var archive = ReadArchive(inputPath);

        safetyBackupDirectory.Create();
        var safetyBackupPath = Path.Combine(
            safetyBackupDirectory.FullName,
            $"FFXIVconfig-before-import-{DateTime.Now:yyyyMMdd-HHmmss-fff}.fea");
        var safetyBackup = Export(configDirectory, safetyBackupPath);

        configDirectory.Create();
        var restoredFileCount = 0;
        var skippedFileCount = 0;

        for (var index = 0; index < RootFileNames.Length; index++)
        {
            var data = archive.RootFiles[index];
            if (data is null)
                continue;

            WriteAtomically(
                Path.Combine(configDirectory.FullName, RootFileNames[index]),
                data,
                null,
                null,
                null);
            restoredFileCount++;
        }

        foreach (var character in archive.Characters)
        {
            var characterDirectory = new DirectoryInfo(Path.Combine(
                configDirectory.FullName,
                $"FFXIV_CHR{character.CharacterId:X16}"));
            characterDirectory.Create();

            foreach (var file in character.Files)
            {
                var destinationPath = Path.Combine(characterDirectory.FullName, file.Name);
                var archivedWriteTime = FromFileTimeUtc(file.LastWriteTime);
                if (
                    preserveNewerFiles
                    && File.Exists(destinationPath)
                    && archivedWriteTime is not null
                    && File.GetLastWriteTimeUtc(destinationPath) > archivedWriteTime.Value
                )
                {
                    skippedFileCount++;
                    continue;
                }

                WriteAtomically(
                    destinationPath,
                    file.Data,
                    FromFileTimeUtc(file.CreationTime),
                    FromFileTimeUtc(file.LastAccessTime),
                    archivedWriteTime);
                restoredFileCount++;
            }

            SetDirectoryWriteTime(characterDirectory, FromFileTimeUtc(character.LastWriteTime));
        }

        return new ImportResult(
            inputPath,
            safetyBackup.Path,
            archive.Characters.Count,
            restoredFileCount,
            skippedFileCount);
    }

    private static BuiltArchive BuildArchive(DirectoryInfo configDirectory)
    {
        var characters = configDirectory.Exists
            ? configDirectory
                .EnumerateDirectories("FFXIV_CHR*", SearchOption.TopDirectoryOnly)
                .Select(TryCreateCharacterSource)
                .Where(x => x is not null)
                .Cast<CharacterSource>()
                .OrderBy(x => x.CharacterId)
                .ToArray()
            : [];

        if (characters.Length > ushort.MaxValue)
            throw new InvalidDataException("Too many character configuration directories.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);

        writer.Write(Magic);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(FormatVersion);
        writer.Write((ushort)0);

        foreach (var rootFileName in RootFileNames)
        {
            var rootFile = new FileInfo(Path.Combine(configDirectory.FullName, rootFileName));
            WriteCompressedBlob(writer, rootFile.Exists ? rootFile : null);
        }

        var fileCount = 0;
        foreach (var character in characters)
        {
            writer.Write(ToFileTimeUtc(character.Directory.LastWriteTimeUtc));
            writer.Write(character.CharacterId);
            writer.Write((uint)character.Files.Length);

            var recordEndOffsetPosition = stream.Position;
            writer.Write(0u);

            foreach (var file in character.Files)
            {
                WriteCharacterFile(writer, file);
                fileCount++;
            }

            PatchUInt32(writer, recordEndOffsetPosition, checked((uint)stream.Position));
        }

        writer.Flush();
        var data = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(4, sizeof(uint)),
            checked((uint)data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(14, sizeof(ushort)),
            checked((ushort)characters.Length));

        uint checksum = 0;
        foreach (var value in data.AsSpan(HeaderSize))
            checksum = unchecked(checksum + value);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(8, sizeof(uint)),
            checksum);

        return new BuiltArchive(data, characters.Length, fileCount);
    }

    private static CharacterSource? TryCreateCharacterSource(DirectoryInfo directory)
    {
        const string Prefix = "FFXIV_CHR";
        if (
            directory.Name.Length != Prefix.Length + 16
            || !directory.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(
                directory.Name.AsSpan(Prefix.Length),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var characterId)
        )
        {
            return null;
        }

        var files = directory
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(x =>
                x.Extension.Equals(".DAT", StringComparison.OrdinalIgnoreCase)
                && IsSafeCharacterFileName(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length > MaxFilesPerCharacter)
            throw new InvalidDataException($"Too many configuration files in {directory.Name}.");

        return new CharacterSource(directory, characterId, files);
    }

    private static void WriteCharacterFile(BinaryWriter writer, FileInfo file)
    {
        writer.Write(ToFileTimeUtc(file.CreationTimeUtc));
        writer.Write(ToFileTimeUtc(file.LastAccessTimeUtc));
        writer.Write(ToFileTimeUtc(file.LastWriteTimeUtc));

        var encodedName = Encoding.Unicode.GetBytes(file.Name + '\0');
        writer.Write((uint)encodedName.Length);
        writer.Write(encodedName);
        WriteCompressedBlob(writer, file);
    }

    private static void WriteCompressedBlob(BinaryWriter writer, FileInfo? file)
    {
        if (file is null)
        {
            writer.Write(0u);
            return;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        using (var input = file.OpenRead())
        {
            input.CopyTo(zlib);
        }

        writer.Write(checked((uint)compressed.Length));
        compressed.Position = 0;
        compressed.CopyTo(writer.BaseStream);
    }

    private static ParsedArchive ReadArchive(string inputPath)
    {
        var file = new FileInfo(Path.GetFullPath(inputPath));
        if (!file.Exists)
            throw new FileNotFoundException("The selected FFXIV settings backup does not exist.", file.FullName);
        if (file.Length is < 24 or > MaxArchiveSize)
            throw new InvalidDataException("The selected file is not a valid FFXIV settings backup.");

        var data = File.ReadAllBytes(file.FullName);
        if (!data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("The selected file has an invalid FFXIV backup signature.");

        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, sizeof(uint)));
        if (declaredLength != data.Length)
            throw new InvalidDataException("The FFXIV backup length is invalid.");

        var storedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, sizeof(uint)));
        uint calculatedChecksum = 0;
        foreach (var value in data.AsSpan(HeaderSize))
            calculatedChecksum = unchecked(calculatedChecksum + value);
        if (storedChecksum != calculatedChecksum)
            throw new InvalidDataException("The FFXIV backup checksum is invalid.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12, sizeof(ushort)));
        if (version != FormatVersion)
            throw new InvalidDataException($"Unsupported FFXIV backup version: {version}.");

        var characterCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(14, sizeof(ushort)));
        if (characterCount > MaxCharacterCount)
            throw new InvalidDataException("The FFXIV backup contains too many characters.");

        var offset = HeaderSize;
        var rootFiles = new byte[]?[RootFileNames.Length];
        for (var index = 0; index < RootFileNames.Length; index++)
            rootFiles[index] = ReadCompressedBlob(data, ref offset);

        var characters = new List<CharacterArchive>(characterCount);
        for (var characterIndex = 0; characterIndex < characterCount; characterIndex++)
        {
            var characterWriteTime = ReadInt64(data, ref offset);
            var characterId = ReadUInt64(data, ref offset);
            var fileCount = ReadUInt32(data, ref offset);
            if (fileCount > MaxFilesPerCharacter)
                throw new InvalidDataException("The FFXIV backup contains too many files for a character.");

            var recordEndOffset = ReadUInt32(data, ref offset);
            if (recordEndOffset < offset || recordEndOffset > data.Length)
                throw new InvalidDataException("The FFXIV backup contains an invalid character record.");

            var files = new List<CharacterFileArchive>(checked((int)fileCount));
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                var creationTime = ReadInt64(data, ref offset);
                var lastAccessTime = ReadInt64(data, ref offset);
                var lastWriteTime = ReadInt64(data, ref offset);

                var nameByteLength = ReadUInt32(data, ref offset);
                if (
                    nameByteLength is < 2 or > MaxFileNameBytes
                    || nameByteLength % 2 != 0
                    || nameByteLength > data.Length - offset
                )
                {
                    throw new InvalidDataException("The FFXIV backup contains an invalid file name.");
                }

                var nameBytes = data.AsSpan(offset, checked((int)nameByteLength));
                offset += checked((int)nameByteLength);
                if (nameBytes[^2] != 0 || nameBytes[^1] != 0)
                    throw new InvalidDataException("The FFXIV backup file name is not terminated.");

                var name = Encoding.Unicode.GetString(nameBytes[..^2]);
                if (!IsSafeCharacterFileName(name))
                    throw new InvalidDataException($"Unsafe FFXIV configuration file name: {name}.");

                var fileData = ReadCompressedBlob(data, ref offset)
                    ?? throw new InvalidDataException($"The FFXIV backup contains no data for {name}.");
                files.Add(new CharacterFileArchive(
                    name,
                    creationTime,
                    lastAccessTime,
                    lastWriteTime,
                    fileData));
            }

            if (offset != recordEndOffset)
                throw new InvalidDataException("The FFXIV backup character record length is invalid.");
            characters.Add(new CharacterArchive(characterId, characterWriteTime, files));
        }

        if (offset != data.Length)
            throw new InvalidDataException("The FFXIV backup has unexpected trailing data.");

        return new ParsedArchive(rootFiles, characters);
    }

    private static byte[]? ReadCompressedBlob(byte[] data, ref int offset)
    {
        var compressedLength = ReadUInt32(data, ref offset);
        if (compressedLength == 0)
            return null;
        if (
            compressedLength > MaxCompressedFileSize
            || compressedLength > data.Length - offset
        )
        {
            throw new InvalidDataException("The FFXIV backup contains an invalid compressed file.");
        }

        using var compressed = new MemoryStream(
            data,
            offset,
            checked((int)compressedLength),
            false);
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var uncompressed = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = zlib.Read(buffer);
            if (read == 0)
                break;
            if (uncompressed.Length + read > MaxUncompressedFileSize)
                throw new InvalidDataException("An FFXIV backup entry is too large.");
            uncompressed.Write(buffer, 0, read);
        }

        if (compressed.Position != compressed.Length)
            throw new InvalidDataException("An FFXIV backup entry has trailing compressed data.");

        offset += checked((int)compressedLength);
        return uncompressed.ToArray();
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        EnsureAvailable(data, offset, sizeof(uint));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        return value;
    }

    private static ulong ReadUInt64(byte[] data, ref int offset)
    {
        EnsureAvailable(data, offset, sizeof(ulong));
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return value;
    }

    private static long ReadInt64(byte[] data, ref int offset)
    {
        return unchecked((long)ReadUInt64(data, ref offset));
    }

    private static void EnsureAvailable(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("The FFXIV backup ended unexpectedly.");
    }

    private static bool IsSafeCharacterFileName(string name)
    {
        if (
            string.IsNullOrWhiteSpace(name)
            || name.Length > 64
            || !name.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(name) != name
        )
        {
            return false;
        }

        return name.All(x =>
            char.IsAsciiLetterOrDigit(x)
            || x is '_' or '-' or '.');
    }

    private static void WriteAtomically(
        string destinationPath,
        byte[] data,
        DateTime? creationTimeUtc,
        DateTime? lastAccessTimeUtc,
        DateTime? lastWriteTimeUtc)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidDataException("The destination path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            TrySetFileTimes(temporaryPath, creationTimeUtc, lastAccessTimeUtc, lastWriteTimeUtc);
            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void TrySetFileTimes(
        string path,
        DateTime? creationTimeUtc,
        DateTime? lastAccessTimeUtc,
        DateTime? lastWriteTimeUtc)
    {
        try
        {
            if (creationTimeUtc is not null)
                File.SetCreationTimeUtc(path, creationTimeUtc.Value);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }

        try
        {
            if (lastAccessTimeUtc is not null)
                File.SetLastAccessTimeUtc(path, lastAccessTimeUtc.Value);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }

        try
        {
            if (lastWriteTimeUtc is not null)
                File.SetLastWriteTimeUtc(path, lastWriteTimeUtc.Value);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static void SetDirectoryWriteTime(DirectoryInfo directory, DateTime? lastWriteTimeUtc)
    {
        if (lastWriteTimeUtc is null)
            return;

        try
        {
            directory.LastWriteTimeUtc = lastWriteTimeUtc.Value;
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static long ToFileTimeUtc(DateTime value)
    {
        try
        {
            return value.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static DateTime? FromFileTimeUtc(long value)
    {
        if (value <= 0)
            return null;

        try
        {
            return DateTime.FromFileTimeUtc(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string EnsureFeaExtension(string path)
    {
        return path.EndsWith(".fea", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".fea";
    }

    private static void PatchUInt32(BinaryWriter writer, long position, uint value)
    {
        var endPosition = writer.BaseStream.Position;
        writer.BaseStream.Position = position;
        writer.Write(value);
        writer.BaseStream.Position = endPosition;
    }

    private sealed record CharacterSource(
        DirectoryInfo Directory,
        ulong CharacterId,
        FileInfo[] Files);

    private sealed record CharacterArchive(
        ulong CharacterId,
        long LastWriteTime,
        IReadOnlyList<CharacterFileArchive> Files);

    private sealed record CharacterFileArchive(
        string Name,
        long CreationTime,
        long LastAccessTime,
        long LastWriteTime,
        byte[] Data);

    private sealed record ParsedArchive(
        byte[]?[] RootFiles,
        IReadOnlyList<CharacterArchive> Characters);

    private sealed record BuiltArchive(
        byte[] Data,
        int CharacterCount,
        int FileCount);

    public sealed record ExportResult(
        string Path,
        int CharacterCount,
        int FileCount,
        long Length);

    public sealed record ImportResult(
        string SourcePath,
        string SafetyBackupPath,
        int CharacterCount,
        int RestoredFileCount,
        int SkippedFileCount);
}
