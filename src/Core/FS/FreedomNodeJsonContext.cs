using System.Text.Json.Serialization;
using FalconNode.Core.FS;
using FalconNode.Core.Social;

namespace FalconNode.Core.FS;


/// <summary>
/// Provides JSON serialization context for FreedomNode, including the FileManifest type.
/// Instructs the source generator to create optimized serialization code for FileManifest.
/// </summary>
[JsonSerializable(typeof(FileManifest))]
[JsonSerializable(typeof(UserProfile))]
public partial class FreedomNodeJsonContext : JsonSerializerContext
{
}