﻿using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using CesiumIonRevitAddin.Gltf;
using CesiumIonRevitAddin.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CesiumIonRevitAddin.Export
{
    public class MaterialCacheDto
    {
        public MaterialCacheDto(string materialName, string uniqueId)
        {
            MaterialName = materialName;
            UniqueId = uniqueId;
        }

        public string MaterialName { get; set; }
        public string UniqueId { get; set; }
    }

    internal class RevitMaterials
    {
        private const string BLEND = "BLEND";
        private const string OPAQUE = "OPAQUE";
        private const int ONEINTVALUE = 1;
        // Temporary number to make texture scaling close to real-world values until permanent fix is in
        private const double magicTextureScalingNumber = 11.11;
        public const string INVALID_MATERIAL = "INVALID_MATERIAL";
        public const string NULL_MATERIAL = "NULL_MATERIAL";


        /// <summary>
        /// Container for material names (Local cache to avoid Revit API I/O)
        /// </summary>
        private static readonly Dictionary<ElementId, MaterialCacheDto> MaterialNameContainer = new Dictionary<ElementId, MaterialCacheDto>();

        private enum GltfBitmapType
        {
            baseColorTexture,
            metallicRoughnessTexture,
            normalTexture,
            occlusionTexture,
            emissiveTexture
        }

        private struct BitmapInfo
        {
            public readonly string AbsolutePath;
            public readonly GltfBitmapType GltfBitmapType;
            public double[] Offset;
            public double? Rotation;
            public double[] Scale;

            public BitmapInfo(string absolutePath, GltfBitmapType gltfBitmapType)
            {
                AbsolutePath = absolutePath;
                GltfBitmapType = gltfBitmapType;
                Offset = Scale = null;
                Rotation = null;
            }
        }

        public readonly static Dictionary<ElementId, GltfMaterial> materialIdDictionary = new Dictionary<ElementId, GltfMaterial>();

        // For debug purposes, may have zero references
#pragma warning disable S1144 // Unused private types or members should be removed
#pragma warning disable IDE0051 // Remove unused private members
        private static void LogMaterialSchemaStats(MaterialNode materialNode, Document document)
#pragma warning restore IDE0051 // Remove unused private members
#pragma warning restore S1144 // Unused private types or members should be removed
        {
            ElementId id = materialNode.MaterialId;

            if (document.GetElement(id) is Material targetMaterial)
            {
                ElementId appearanceAssetId = targetMaterial.AppearanceAssetId;
                AppearanceAssetElement appearanceAssetElem = document.GetElement(appearanceAssetId) as AppearanceAssetElement;
                Asset renderingAsset = appearanceAssetElem.GetRenderingAsset();

                // it seems asset.Name is typically a schema
                var schema = renderingAsset.Name;
                if (renderingAsset.FindByName("BaseSchema") is AssetPropertyString baseSchema)
                {
                    schema = baseSchema.Value;
                }
                Logger.Instance.Log("Material schema: " + schema);
            }
        }

        public static void Export(MaterialNode materialNode,
            Document doc,
            IndexedDictionary<GltfMaterial> materials,
            GltfExtStructuralMetadataExtensionSchema extStructuralMetadataExtensionSchema,
            IndexedDictionary<GltfSampler> samplers,
            IndexedDictionary<GltfImage> images,
            IndexedDictionary<GltfTexture> textures,
            ref bool materialHasTexture,
            Preferences preferences)
        {
            ElementId id = materialNode.MaterialId;

            // Validate if the material is valid because for some reason there are
            // materials with invalid Ids
            if (id == ElementId.InvalidElementId)
            {
                materials.AddOrUpdateCurrentMaterial(INVALID_MATERIAL, new GltfMaterial { Name = "Revit Invalid Material" }, false);
                return;
            }

            if (doc.GetElement(materialNode.MaterialId) == null)
            {
                materials.AddOrUpdateCurrentMaterial(NULL_MATERIAL, new GltfMaterial { Name = "Revit Null Material" }, false);
                return;
            }

            if (preferences.VerboseLogging) Logger.Instance.Log("Exporting MaterialId " + id.ToString());

            string uniqueId;
            if (materialIdDictionary.TryGetValue(id, out GltfMaterial gltfMaterial))
            {
                Element materialElement = doc.GetElement(materialNode.MaterialId);
                gltfMaterial.Name = materialElement.Name;
                uniqueId = materialElement.UniqueId;
                materialHasTexture = gltfMaterial.PbrMetallicRoughness.BaseColorTexture != null;
            }
            else
            {
                gltfMaterial = new GltfMaterial();
                float opacity = ONEINTVALUE - (float)materialNode.Transparency;

                if (!(doc.GetElement(materialNode.MaterialId) is Material material))
                {
                    return;
                }

                string materialGltfName = Utils.Util.GetGltfName(material.Name);

                if (preferences.ExportMetadata)
                {
                    gltfMaterial.Extensions = gltfMaterial.Extensions ?? new GltfExtensions();
                    gltfMaterial.Extensions.EXT_structural_metadata = gltfMaterial.Extensions.EXT_structural_metadata ?? new ExtStructuralMetadata();
                    gltfMaterial.Extensions.EXT_structural_metadata.Class = materialGltfName;

                    var classSchema = extStructuralMetadataExtensionSchema.GetClass(materialGltfName) ?? extStructuralMetadataExtensionSchema.AddClass(material.Name);
                    ParameterSetIterator paramIterator = material.Parameters.ForwardIterator();
                    while (paramIterator.MoveNext())
                    {
                        var parameter = (Parameter)paramIterator.Current;
                        if (parameter.HasValue)
                        {
                            string paramName = parameter.Definition.Name;

                            ParameterValue paramValue = Util.GetParameterValue(parameter);

                            if (Util.ShouldFilterMetadata(paramValue)) continue;

                            string paramGltfName = Utils.Util.GetGltfName(paramName);

                            if (!gltfMaterial.Extensions.EXT_structural_metadata.HasProperty(paramGltfName))
                            {
                                gltfMaterial.Extensions.EXT_structural_metadata.AddProperty(paramGltfName, paramValue);
                                AddParameterToClassSchema(parameter, classSchema);
                            }
                        }
                    }

                    AddMaterialRenderingPropertiesToSchema(material, doc, gltfMaterial, extStructuralMetadataExtensionSchema);
                }

                if (MaterialNameContainer.TryGetValue(materialNode.MaterialId, out MaterialCacheDto elementData))
                {
                    gltfMaterial.Name = elementData.MaterialName;
                    uniqueId = elementData.UniqueId;
                }
                else
                {
                    // construct a material from the node
                    Element materialElement = doc.GetElement(materialNode.MaterialId);
                    gltfMaterial.Name = materialElement.Name;
                    uniqueId = materialElement.UniqueId;
                    MaterialNameContainer.Add(materialNode.MaterialId, new MaterialCacheDto(materialElement.Name, materialElement.UniqueId));
                }

                var gltfPbr = new GltfPbr();
                SetGltfMaterialsProperties(materialNode, opacity, ref gltfPbr, ref gltfMaterial);

                List<BitmapInfo> bitmapInfoCollection = GetBitmapInfo(doc, material);

                materialHasTexture = preferences.Textures && bitmapInfoCollection.Count > 0;
                if (materialHasTexture)
                {
                    if (samplers.List.Count == 0)
                    {
                        samplers.AddOrUpdateCurrent("defaultSampler", new GltfSampler());
                    }

                    foreach (BitmapInfo bitmapInfo in bitmapInfoCollection)
                    {
                        if (bitmapInfo.GltfBitmapType == GltfBitmapType.baseColorTexture)
                        {
                            var rawFileName = Path.GetFileName(bitmapInfo.AbsolutePath);

                            int imageIndex;
                            if (images.Contains(rawFileName))
                            {
                                imageIndex = images.GetIndexFromUuid(rawFileName);
                            }
                            else
                            {
                                var copiedFilePath = Path.Combine(preferences.TempDirectory, rawFileName);

                                MaterialUtils.SaveDownsampledTexture(bitmapInfo.AbsolutePath, copiedFilePath, preferences.MaxTextureSize, preferences.MaxTextureSize);

                                var gltfImage = new GltfImage
                                {
                                    Uri = Uri.EscapeDataString(rawFileName)
                                };
                                images.AddOrUpdateCurrent(rawFileName, gltfImage);

                                // assuming one-to-one mapping between glTF images and texture arrays
                                imageIndex = images.GetIndexFromUuid(rawFileName);
                                var gltfTexture = new GltfTexture
                                {
                                    Sampler = 0,
                                    Source = imageIndex
                                };
                                textures.AddOrUpdateCurrent(rawFileName, gltfTexture);
                            }

                            gltfMaterial.PbrMetallicRoughness.BaseColorTexture = new GltfTextureInfo
                            {
                                Index = imageIndex,
                                TexCoord = 0
                            };

                            KhrTextureTransform khrTextureTransformExtension;
                            if (gltfMaterial.PbrMetallicRoughness.BaseColorTexture.Extensions.TryGetValue("KHR_texture_transform", out var extension))
                            {
                                khrTextureTransformExtension = (KhrTextureTransform)extension;
                            }
                            else
                            {
                                khrTextureTransformExtension = new KhrTextureTransform();
                                gltfMaterial.PbrMetallicRoughness.BaseColorTexture.Extensions.Add("KHR_texture_transform", khrTextureTransformExtension);
                            }
                            khrTextureTransformExtension.Offset = bitmapInfo.Offset;
                            khrTextureTransformExtension.Rotation = bitmapInfo.Rotation;
                            khrTextureTransformExtension.Scale = bitmapInfo.Scale;
                        }
                    }
                }

                materialIdDictionary.Add(id, gltfMaterial);
            }

            materials.AddOrUpdateCurrentMaterial(uniqueId, gltfMaterial, false);
        }

        private static List<BitmapInfo> GetBitmapInfo(Document document, Material material)
        {
            var attachedBitmapInfo = new List<BitmapInfo>();

            if (document.GetElement(material.Id) is Material targetMaterial)
            {
                ElementId appearanceAssetId = targetMaterial.AppearanceAssetId;
                // Some (physical) materials do not link to render materials (Appearances in Revit-speak).
                // This has happened with structural elements.
                // Exit if this is the case.

                if (Util.GetElementIdAsLong(appearanceAssetId) == -1) return attachedBitmapInfo;

                AppearanceAssetElement appearanceAssetElem = document.GetElement(appearanceAssetId) as AppearanceAssetElement;
                Asset renderingAsset = appearanceAssetElem.GetRenderingAsset();

                string schema;
                if (renderingAsset.FindByName("BaseSchema") is AssetPropertyString baseSchema)
                {
                    schema = baseSchema.Value;
                }
                else
                {
                    // it seems asset.Name is typically a schema
                    schema = renderingAsset.Name;
                }

                // schema list at https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Material_General_Material_Information_html
                // apparently not exhaustive: missing "PrismOpaqueSchema"
                // nevermind. "PrismOpaqueSchema" seems to have become "AdvancedOpaque"
                switch (schema)
                {
                    case "PrismOpaqueSchema":
                    case "AdvancedOpaque":
                        attachedBitmapInfo = ParsePrismOpaqueSchema(renderingAsset);
                        break;
                    case "HardwoodSchema":
                        attachedBitmapInfo = ParseHardwoodSchema(renderingAsset);
                        break;
                    case "GenericSchema":
                        attachedBitmapInfo = ParseGenericSchema(renderingAsset);
                        break;
                    default:
                        Logger.Instance.Log("skipping material processing of unknown material schema type: " + schema);
                        break;
                }
            }

            return attachedBitmapInfo;
        }

        // https://help.autodesk.com/view/RVT/2022/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Material_Material_Schema_Prism_Schema_Opaque_html
        private static List<BitmapInfo> ParsePrismOpaqueSchema(Asset renderingAsset)
        {
            AssetProperty baseColorProperty = renderingAsset.FindByName(AdvancedOpaque.OpaqueAlbedo);
            List<BitmapInfo> bitmapInfoCollection = GetBitmapInfoCollection(baseColorProperty);
            return bitmapInfoCollection;
        }

        private static List<BitmapInfo> GetBitmapInfoCollection(AssetProperty baseColorProperty)
        {
            var bitmapInfoCollection = new List<BitmapInfo>();

            if (baseColorProperty.NumberOfConnectedProperties < 1)
            {
                return bitmapInfoCollection;
            }

            var connectedProperty = baseColorProperty.GetConnectedProperty(0) as Asset;

            AssetPropertyString path = connectedProperty.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) as AssetPropertyString;
            // Procedurally generated textures (e.g., NoiseSchema) will not have bitmaps.
            if (path == null) return bitmapInfoCollection;

            var absolutePath = GetAbsoluteMaterialPath(path.Value);
            // It's possible for a bitmap object propery to have a path to a texture file, but that
            // file is not on the disk. The can happen if the texture patch and the model is being exported
            // on another machine that doesn't have the materials installed in that location. Test for this case.
            if (absolutePath == null)
            {
                Logger.Instance.Log("Could not find the following texture: " + path.Value);
                return bitmapInfoCollection;
            }
            BitmapInfo baseColor = new BitmapInfo(absolutePath, GltfBitmapType.baseColorTexture);

            AddTextureTransformInfo(ref baseColor, connectedProperty);

            bitmapInfoCollection.Add(baseColor);

            return bitmapInfoCollection;
        }

        private static List<BitmapInfo> ParseGenericSchema(Asset renderingAsset)
        {
            AssetProperty baseColorProperty = renderingAsset.FindByName("generic_diffuse");
            List<BitmapInfo> bitmapInfoCollection = GetBitmapInfoCollection(baseColorProperty);
            return bitmapInfoCollection;
        }

        // https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Material_Material_Schema_Protein_Hardwood_Schema_html
        private static List<BitmapInfo> ParseHardwoodSchema(Asset renderingAsset)
        {
            AssetProperty baseColorProperty = renderingAsset.FindByName(Hardwood.HardwoodColor);
            List<BitmapInfo> bitmapInfoCollection = GetBitmapInfoCollection(baseColorProperty);
            return bitmapInfoCollection;
        }

        // See https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Revit_Geometric_Elements_Material_Material_Schema_Other_Schema_UnifiedBitmap_html
        private static void AddTextureTransformInfo(ref BitmapInfo bitmapInfo, Asset connectedProperty)
        {
            bitmapInfo.Offset = new double[]
            {
                connectedProperty.FindByName(UnifiedBitmap.TextureRealWorldOffsetX) is AssetPropertyDistance xOffset
                    ? xOffset.Value * magicTextureScalingNumber : 0,
                connectedProperty.FindByName(UnifiedBitmap.TextureRealWorldOffsetY) is AssetPropertyDistance yOffset
                    ? yOffset.Value * magicTextureScalingNumber : 0
            };

            var rotation = connectedProperty.FindByName(UnifiedBitmap.TextureWAngle) as AssetPropertyDouble;
            bitmapInfo.Rotation = rotation.Value;

            bitmapInfo.Scale = new double[]
            {
                connectedProperty.FindByName(UnifiedBitmap.TextureRealWorldScaleX) is AssetPropertyDistance xScale
                    ? 1.0 / xScale.Value * magicTextureScalingNumber : 1.0,
                connectedProperty.FindByName(UnifiedBitmap.TextureRealWorldScaleY) is AssetPropertyDistance yScale
                    ? 1.0 / yScale.Value * magicTextureScalingNumber : 1.0
            };
        }

        private static readonly string baseCommonPath = Path.Combine("Common Files", "Autodesk Shared", "Materials", "Textures");
        private static readonly string[] possiblePathExtensions = new string[] {
            baseCommonPath,
            Path.Combine(baseCommonPath, "1", "Mats"),
            Path.Combine(baseCommonPath, "2", "Mats"),
            Path.Combine(baseCommonPath, "3", "Mats")
        };

        private static string GetAbsoluteMaterialPath(string relativeOrAbsolutePath)
        {
            string[] allPaths = relativeOrAbsolutePath.Split('|');
            allPaths = allPaths.Distinct().ToArray();
            relativeOrAbsolutePath = allPaths[allPaths.Length - 1];

            if (Path.IsPathRooted(relativeOrAbsolutePath) && File.Exists(relativeOrAbsolutePath))
            {
                return relativeOrAbsolutePath;
            }

            string programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86Path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            List<string> possibleBasePaths = new List<string>();
            foreach (string possiblePathExtension in possiblePathExtensions)
            {
                possibleBasePaths.Add(System.IO.Path.Combine(programFilesX86Path, possiblePathExtension));
                possibleBasePaths.Add(System.IO.Path.Combine(programFilesPath, possiblePathExtension));
            }
            possibleBasePaths.AddRange(GetAdditionalRenderAppearancePaths());

            foreach (string basePath in possibleBasePaths)
            {
                if (!string.IsNullOrEmpty(basePath))
                {
                    string fullPath = Path.Combine(basePath, relativeOrAbsolutePath);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }

            return null;
        }

        private static List<string> GetAdditionalRenderAppearancePaths()
        {
            List<string> paths = new List<string>();
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string revitBasePath = Path.Combine(appDataPath, "Autodesk", "Revit");

            // Find all directories in the Revit Appdata folder that contain a Revit.ini file
            foreach (var directory in Directory.GetDirectories(revitBasePath, "Autodesk Revit 20??"))
            {
                string revitIniPath = Path.Combine(directory, "Revit.ini");

                if (File.Exists(revitIniPath))
                {
                    foreach (var line in File.ReadAllLines(revitIniPath))
                    {
                        // Look for the AdditionalRenderAppearancePath setting
                        if (line.StartsWith("AdditionalRenderAppearancePath="))
                        {
                            paths.Add(line.Split('=')[1].Trim());
                        }
                    }
                }
            }

            return paths;
        }

        private static void AddMaterialRenderingPropertiesToSchema(Autodesk.Revit.DB.Material material, Document doc, GltfMaterial gltfMaterial,
            GltfExtStructuralMetadataExtensionSchema extStructuralMetadataSchema)
        {
            if (!(doc.GetElement(material.AppearanceAssetId) is AppearanceAssetElement assetElement))
            {
                gltfMaterial.Extensions.EXT_structural_metadata.AddProperty("AppearanceAsset", "None");
                return;
            }

            Asset renderingAsset = assetElement.GetRenderingAsset();
            if (renderingAsset == null)
            {
                gltfMaterial.Extensions.EXT_structural_metadata.AddProperty("RenderingAsset", "None");
                return;
            }

            var materialGltfName = Utils.Util.GetGltfName(material.Name);

            var schemaClasses = extStructuralMetadataSchema.GetClasses();
            Dictionary<string, object> classSchema;
            Dictionary<string, object> classPropertiesSchema;
            if (schemaClasses.ContainsKey(materialGltfName))
            {
                classSchema = extStructuralMetadataSchema.GetClass(materialGltfName);
                classPropertiesSchema = (Dictionary<string, object>)classSchema["properties"];
            }
            else
            {
                classSchema = extStructuralMetadataSchema.AddClass(material.Name);
                classPropertiesSchema = new Dictionary<string, object>();
            }

            for (int i = 0; i < renderingAsset.Size; i++)
            {
                AssetProperty property = renderingAsset.Get(i);
                if (property is AssetPropertyString assetPropertyString)
                {
                    if (Util.ShouldFilterMetadata(assetPropertyString.Value)) continue;

                    string gltfPropertyName = Util.GetGltfName(assetPropertyString.Name);

                    if (!gltfMaterial.Extensions.EXT_structural_metadata.HasProperty(gltfPropertyName))
                    {
                        gltfMaterial.Extensions.EXT_structural_metadata.AddProperty(gltfPropertyName, assetPropertyString.Value);
                    }
                    else
                    {
                        // A property from the Appearance can have the same name as one from the physical Material.
                        // For example, "category".
                        gltfPropertyName = string.Concat("render", char.ToUpper(gltfPropertyName[0]), gltfPropertyName.Substring(1));
                        if (!gltfMaterial.Extensions.EXT_structural_metadata.HasProperty(gltfPropertyName))
                        {
                            gltfMaterial.Extensions.EXT_structural_metadata.AddProperty(gltfPropertyName, assetPropertyString.Value);
                        }
                    }

                    // add to schema
                    if (!classPropertiesSchema.ContainsKey(gltfPropertyName))
                    {
                        classPropertiesSchema.Add(gltfPropertyName, new Dictionary<string, object>());
                        var schemaProperty = (Dictionary<string, object>)classPropertiesSchema[gltfPropertyName];

                        if (!schemaProperty.ContainsKey("name"))
                        {
                            schemaProperty.Add("name", assetPropertyString.Name);
                        }

                        AssetPropertyType assetPropertyType = property.Type;
                        switch (assetPropertyType)
                        {
                            case AssetPropertyType.String:
                                schemaProperty.Add("type", "STRING");
                                break;
                            case AssetPropertyType.Integer:
                                schemaProperty.Add("type", "SCALAR");
                                schemaProperty.Add("componentType", "INT32");
                                break;
                            case AssetPropertyType.Float:
                            case AssetPropertyType.Double1:
                                schemaProperty.Add("type", "SCALAR");
                                schemaProperty.Add("componentType", "FLOAT32");
                                break;
                            default:
                                Logger.Instance.Log("Cannot parse AssetPropertyType " + assetPropertyType.ToString());
                                break;
                        }

                        schemaProperty.Add("required", GltfExtStructuralMetadataExtensionSchema.IsRequired(assetPropertyString.Name));
                    }
                }
            }

            if (classPropertiesSchema.Count > 0) classSchema["properties"] = classPropertiesSchema;
        }

        private static void AddParameterToClassSchema(Parameter parameter, Dictionary<string, object> classSchema)
        {
            var gltfPropertyName = Utils.Util.GetGltfName(parameter.Definition.Name);

            if (!classSchema.TryGetValue("properties", out var classSchemaOut))
            {
                classSchemaOut = new Dictionary<string, object>();
                classSchema.Add("properties", classSchemaOut);
            }
            var classSchemaProperties = (Dictionary<string, object>)classSchemaOut;

            if (!classSchemaProperties.ContainsKey(gltfPropertyName))
            {
                var propertySchema = new Dictionary<string, object>();
                classSchemaProperties.Add(gltfPropertyName, propertySchema);
                propertySchema.Add("name", parameter.Definition.Name);

                switch (parameter.StorageType)
                {
                    case StorageType.Double:
                        propertySchema.Add("type", "SCALAR");
                        propertySchema.Add("componentType", "FLOAT32");
                        break;
                    case StorageType.Integer:
                    case StorageType.ElementId:
                        propertySchema.Add("type", "SCALAR");
                        propertySchema.Add("componentType", "INT32");
                        break;
                    case StorageType.String:
                        propertySchema.Add("type", "STRING");
                        break;
                    default:
                        break;
                }

                propertySchema.Add("required", false);
            }
        }

        private static void SetGltfMaterialsProperties(MaterialNode materialNode, float opacity, ref GltfPbr gltfPbr, ref GltfMaterial gltfMaterial)
        {
            // Some materials have an invalid color.  In this case, we use a default color.
            Color baseColor = materialNode.Color.IsValid ? materialNode.Color : new Color(128, 128, 128);

            gltfPbr.BaseColorFactor = new List<float>(4) { baseColor.Red / 255f, baseColor.Green / 255f, baseColor.Blue / 255f, opacity };
            gltfPbr.MetallicFactor = 0f;
            gltfPbr.RoughnessFactor = opacity < 1f ? 0.5f : 1f;
            gltfMaterial.PbrMetallicRoughness = gltfPbr;

            gltfMaterial.AlphaMode = opacity < 1f ? BLEND : OPAQUE;
            gltfMaterial.AlphaCutoff = null;
        }
    }
}
