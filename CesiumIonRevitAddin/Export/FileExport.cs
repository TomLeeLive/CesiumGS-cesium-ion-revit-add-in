﻿using System.IO;
using System.Collections.Generic;
using CesiumIonRevitAddin.Export;
using Autodesk.Revit.DB.DirectContext3D;

namespace CesiumIonRevitAddin.Gltf
{
    internal class FileExport
    {
        public static void Run(
            Preferences preferences,
            List<GltfBufferView> bufferViews,
            List<GltfBuffer> buffers,
            List<GltfBinaryData> binaryData,
            List<GltfScene> scenes,
            IndexedDictionary<GltfNode> nodes,
            IndexedDictionary<GltfMesh> meshes,
            IndexedDictionary<GltfMaterial> materials,
            List<GltfAccessor> accessors,
            List<string> extensionsUsed,
            Dictionary<string, GltfExtensionSchema> extensions,
            GltfVersion asset,
            IndexedDictionary<GltfImage> images,
            IndexedDictionary<GltfTexture> textures,
            IndexedDictionary<GltfSampler> samplers)
        {
            // TODO: needed? create extensions schema

            // DEBUG
            int bufferByteLength = buffers[0].ByteLength;

            string outputPath = Path.GetDirectoryName(preferences.OutputPath) + "\\" + Path.GetFileNameWithoutExtension(preferences.OutputPath);

            string binFileName = outputPath + ".bin";

            BufferConfig.Run(bufferViews, buffers, binFileName);
            BinFile.Create(binFileName, binaryData, preferences.Normals, false);

            string gltfJson = GltfJson.Get(scenes, nodes.List, meshes.List, materials.List,
                buffers, bufferViews, accessors, extensionsUsed, extensions, preferences, asset, images.List, textures.List, samplers.List);

            string gltfFileName = outputPath + ".gltf";
            File.WriteAllText(gltfFileName, gltfJson);
        }
    }
}
