using System.Text;
using K4os.Compression.LZ4;
using Serilog;

namespace Ovjo
{
    public class PackageEntry(string fileName, byte[] data)
    {
        public string FileName = fileName;
        public byte[] Data = data;
    }

    public class World
    {
        public const ushort CurrentVersion = 0;
        public const string DefaultPath = ".ovjowld";

        private readonly byte[] MagicHeader = Encoding.ASCII.GetBytes("OVJO.WORLD");
        public List<PackageEntry> Packages = [];
        public Overdare.Map Map;

        public World(Overdare.Map map)
        {
            Map = map;
        }

        public World(BinaryReader reader)
        {
            var readMagicHeader = reader.ReadBytes(MagicHeader.Length);
            Log.Debug(
                $"{BitConverter.ToString(readMagicHeader)} == {BitConverter.ToString(MagicHeader)}"
            );
            if (!readMagicHeader.SequenceEqual(MagicHeader))
            {
                throw new InvalidDataException(
                    "Invalid ovjo world file format. Got an invalid magic header."
                );
            }
            var version = reader.ReadUInt16();
            if (version != CurrentVersion)
                throw new NotSupportedException($"Unsupported world version: {version}");

            var compressedLength = reader.ReadInt32();
            var uncompressedLength = reader.ReadInt32();

            var compressed = reader.ReadBytes(compressedLength);
            var uncompressed = new byte[uncompressedLength];
            {
                var decoded = LZ4Codec.Decode(
                    compressed,
                    0,
                    compressedLength,
                    uncompressed,
                    0,
                    uncompressedLength
                );
                if (decoded != uncompressedLength)
                    throw new InvalidDataException("Failed to decompress chunk data");
            }

            {
                var mapChunkSize = reader.ReadInt32();
                Log.Debug("Map chunk size: {MapChunkSize}", mapChunkSize);
                Log.Debug($"Uncompressed length: {uncompressed.Length}");
                if (mapChunkSize < 0 || mapChunkSize > uncompressedLength)
                {
                    throw new InvalidDataException("Invalid map chunk size in ovjo world file.");
                }
                var mapChunkData = new byte[mapChunkSize];
                Array.Copy(uncompressed, 0, mapChunkData, 0, mapChunkSize);
                Map = new(mapChunkData);
            }

            var packageCount = reader.ReadByte();
            for (int i = 0; i < packageCount; i++)
            {
                var fileName = reader.ReadString();
                var offset = reader.ReadInt32();
                var size = reader.ReadInt32();

                var data = new byte[size];
                Array.Copy(uncompressed, offset, data, 0, size);
                Packages.Add(new(fileName, data));
            }
        }

        public World(Stream stream)
            : this(new BinaryReader(stream)) { }

        public World(byte[] buffer)
            : this(new MemoryStream(buffer)) { }

        public static World Open(string path)
        {
            using var stream = File.OpenRead(path);
            return new(stream);
        }

        public static World Open()
        {
            return Open(DefaultPath);
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(MagicHeader);
            writer.Write(CurrentVersion);

            using var chunksStream = Map.WriteData();
            int mapChunkSize = (int)chunksStream.Length;
            Log.Debug($"Map chunk size: {mapChunkSize}");
            List<(string FileName, int Offset, int Size)> packageEntries = [];
            using BinaryWriter chunksWriter = new(chunksStream);
            foreach (var package in Packages)
            {
                chunksStream.Position = chunksStream.Length;
                packageEntries.Add(
                    (package.FileName, Offset: (int)chunksStream.Length, Size: package.Data.Length)
                );
                chunksWriter.Write(package.Data);
                Log.Debug($"Package chunk written: {package.Data.Length}");
            }
            byte[] uncompressed = chunksStream.ToArray();
            byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(uncompressed.Length)];
            int compressedLength = LZ4Codec.Encode(
                uncompressed,
                0,
                uncompressed.Length,
                compressed,
                0,
                compressed.Length
            );
            Array.Resize(ref compressed, compressedLength);

            writer.Write(compressed.Length);
            writer.Write(uncompressed.Length);

            Log.Debug($"uncompressed length: {uncompressed.Length} map size: {mapChunkSize}");

            writer.Write(compressed);

            writer.Write(mapChunkSize);

            writer.Write((byte)packageEntries.Count);
            foreach (var (FileName, Offset, Size) in packageEntries)
            {
                writer.Write(FileName);
                writer.Write(Offset);
                writer.Write(Size);
            }
        }

        public void Write(Stream stream)
        {
            using var writer = new BinaryWriter(stream);
            Write(writer);
        }

        public void Save(string path)
        {
            using var stream = File.Create(path);
            Write(stream);
        }

        public void Save()
        {
            Save(DefaultPath);
        }

        private static string GetWorldDirectoryName(string path)
        {
            return Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Invalid world path.");
        }

        public static World FromOverdare(string path)
        {
            World world = new(Overdare.Map.Open(path));

            var worldDir = GetWorldDirectoryName(path);
            foreach (var entryPath in Directory.GetFiles(worldDir))
            {
                if (
                    Path.GetFullPath(path)
                        .Equals(Path.GetFullPath(entryPath), StringComparison.OrdinalIgnoreCase)
                )
                {
                    continue; // Skip the map file itself
                }

                Log.Debug($"collected PackageEntry {entryPath}");

                var fileName = Path.GetFileName(entryPath);
                var data = File.ReadAllBytes(entryPath);
                switch (Path.GetExtension(entryPath).ToLowerInvariant())
                {
                    case ".json":
                        data = MessagePack.MessagePackSerializer.ConvertFromJson(
                            Encoding.UTF8.GetString(data)
                        );
                        break;
                    case ".lock":
                        continue;
                }
                world.Packages.Add(new(fileName, data));
            }

            return world;
        }

        public void ExportAsOverdare(string path)
        {
            Map.Save(path);

            var worldDir = GetWorldDirectoryName(path);
            foreach (var package in Packages)
            {
                var filePath = Path.Combine(worldDir, package.FileName);
                var data = package.Data;
                switch (Path.GetExtension(package.FileName).ToLowerInvariant())
                {
                    case ".json":
                        data = Encoding.UTF8.GetBytes(
                            MessagePack.MessagePackSerializer.ConvertToJson(data)
                        );
                        break;
                    case ".lock":
                        continue;
                }
                File.WriteAllBytes(filePath, data);
            }
        }
    }
}
