﻿using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using CesiumIonRevitAddin.Utils;
using CesiumIonRevitAddin.Model;
using System.Windows.Controls;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using System.Security.Policy;
using System.Data.SqlClient;
using System.Xml.Linq;
using Autodesk.Revit.UI;
using System.Reflection;
using System.Threading;
using CesiumIonRevitAddin.Export;
using Autodesk.Revit.DB.Visual;
using System.Linq;
using static CesiumIonRevitAddin.Gltf.GltfExportContext;

namespace CesiumIonRevitAddin.Gltf
{
    internal class GltfExportContext : IPhotoRenderContext
    {

        const char UNDERSCORE = '_';
        bool verboseLog = true;

        public GltfExportContext(Document doc)
        {
            documents.Add(doc);
            view = doc.ActiveView;
        }

        View view;
        Autodesk.Revit.DB.Element element;
        List<Document> documents = new List<Document>();
        bool cancelation;
        Stack<Autodesk.Revit.DB.Transform> transformStack = new Stack<Autodesk.Revit.DB.Transform>();
        GltfNode rootNode;
        readonly GltfVersion gltfVersion = new GltfVersion();
        // TODO: readonly?
        List<GltfAccessor> accessors = new List<GltfAccessor>();
        List<GltfBufferView> bufferViews = new List<GltfBufferView>();
        List<GltfBuffer> buffers = new List<GltfBuffer>();
        List<GltfBinaryData> binaryFileData = new List<GltfBinaryData>();
        List<GltfScene> scenes = new List<GltfScene>();

        IndexedDictionary<GltfNode> nodes = new IndexedDictionary<GltfNode>();
        IndexedDictionary<GltfMesh> meshes = new IndexedDictionary<GltfMesh>();
        IndexedDictionary<GltfMaterial> materials = new IndexedDictionary<GltfMaterial>();
        IndexedDictionary<GltfImage> images = new IndexedDictionary<GltfImage>();
        IndexedDictionary<GltfSampler> samplers = new IndexedDictionary<GltfSampler>();
        IndexedDictionary<GltfTexture> textures = new IndexedDictionary<GltfTexture>();

        List<string> extensionsUsed = new List<string>();
        Dictionary<string, GltfExtensionSchema> extensions = new Dictionary<string, GltfExtensionSchema>();
        GltfExtStructuralMetadataExtensionSchema extStructuralMetadata = new GltfExtStructuralMetadataExtensionSchema();

        Document Doc
        {
            get
            {
                // return linked doc if count > 1
                return documents.Count == 1 ? documents[0] : documents[1];
            }
        }

        // TODO: make export not a singleton and remove Reset()
        void Reset()
        {
            RevitMaterials.materialIdDictionary.Clear();
        }

        public bool Start()
        {
            Logger.Enabled = verboseLog;
            cancelation = false;

            Reset();

            transformStack.Push(Autodesk.Revit.DB.Transform.Identity);

            var preferences = new Preferences(); // TODO: user user-defined preferences
            rootNode = new GltfNode
            {
                Name = "rootNode",
                // Rotation = CesiumIonRevitAddin.Transform.ModelRotation.Get(preferences.FlipAxis)
            };

            ProjectInfo projectInfo = Doc.ProjectInformation;

            gltfVersion.extras.Add("Project Name", projectInfo.Name);
            gltfVersion.extras.Add("Project Number", projectInfo.Number);
            gltfVersion.extras.Add("Client Name", projectInfo.ClientName);
            gltfVersion.extras.Add("Project Address", projectInfo.Address);
            gltfVersion.extras.Add("Building Name", projectInfo.BuildingName);
            gltfVersion.extras.Add("Author", projectInfo.Author);
            gltfVersion.extras.Add("Organization Name", projectInfo.OrganizationName);
            gltfVersion.extras.Add("Organization Description", projectInfo.OrganizationDescription);
            gltfVersion.extras.Add("Issue Date", projectInfo.IssueDate);
            gltfVersion.extras.Add("Project Status", projectInfo.Status);

            BindingMap bindingMap = Doc.ParameterBindings;
            var iterator = bindingMap.ForwardIterator();
            while (iterator.MoveNext())
            {
                var definition = iterator.Key;
                // InstanceBinding and TypeBinding don't seem to have any additional members vs ELementBinding base class
                var elementBinding = (ElementBinding)iterator.Current;

                // Get parameter definition information
                string paramDefinitionName = definition.Name;

                // TODO: handle ExternalDefinition
                var internalDefinition = definition as InternalDefinition;
                if (internalDefinition != null)
                {
                    // TODO: keep?
                    // gltfVersion.extras.Add(paramDefinitionName, categoryNames);

                    var forgeTypeId = internalDefinition.GetDataType();
                    // String^ glTFDataType = "";
                    var isMeasurable = UnitUtils.IsMeasurableSpec(forgeTypeId);

                    var categoryGltfProperty = new Dictionary<string, Object>
                    {
                        { "name", definition.Name },
                        { "required", false } // TODO: unsure if false
                    };

                    if (isMeasurable) // TODO: isMeasurable SHOULD equate to Revit's StorageType::Double
                    {
                        //glTFDataType = "SCALAR:Double";
                        categoryGltfProperty.Add("type", "SCALAR");
                        categoryGltfProperty.Add("componentType", "FLOAT32");
                    }
                    else if (forgeTypeId == SpecTypeId.String.Text)
                    {
                        categoryGltfProperty.Add("type", "STRING");
                    }
                    else if (forgeTypeId == SpecTypeId.Int.Integer)
                    {
                        categoryGltfProperty.Add("type", "SCALAR");
                        categoryGltfProperty.Add("componentType", "INT32");
                    }
                    else
                    {
                        // no mapped ForgeTypeId
                        Logger.Instance.Log("no mapped ForgeTypeId: " + definition.Name);
                        continue;
                    }

                    var categories = elementBinding.Categories;
                    foreach (var obj in categories)
                    {
                        var category = (Category)obj;
                        var categoryGltfName = CesiumIonRevitAddin.Utils.Util.GetGltfName(category.Name);
                        // categoryNames += category->Name + "; ";

                        extStructuralMetadata.AddCategory(category.Name);
                        var class_ = extStructuralMetadata.GetClass(categoryGltfName);
                        var schemaProperties = extStructuralMetadata.GetProperties(class_);
                        schemaProperties.Add(CesiumIonRevitAddin.Utils.Util.GetGltfName(definition.Name), categoryGltfProperty);
                    }

                    // TODO
                    // gltfVersion.extras.Add(paramDefinitionName + "_glTFType", glTFDataType);

                    //// DEVEL
                    //if (UnitUtils::IsMeasurableSpec(forgeTypeId)) {
                    //	auto discipline = UnitUtils::GetDiscipline(forgeTypeId);
                    //	gltfVersion.extras.Add(paramDefinitionName + "_discipline", discipline->TypeId);
                    //	auto typeCatalog = UnitUtils::GetTypeCatalogStringForSpec(forgeTypeId);
                    //	gltfVersion.extras.Add(paramDefinitionName + "_typeCatalog", typeCatalog);
                    //}
                    //auto isUnit = UnitUtils::IsUnit(forgeTypeId);
                    //gltfVersion.extras.Add(paramDefinitionName + "_isUnit", isUnit);
                    //auto isSymbol = UnitUtils::IsSymbol(forgeTypeId);
                    //gltfVersion.extras.Add(paramDefinitionName + "_isSymbol", isSymbol);

                    // https://forums.autodesk.com/t5/revit-api-forum/conversion-from-internal-to-revit-type-for-unittype/td-p/10452742
                    // "For context, the ForgeTypeId properties directly in the SpecTypeId class identify the measurable data types, like SpecTypeId.Length or SpecTypeId.Mass. "
                    // maybe use SpecTypeId comparisons to get data type
                    //auto isSpec = SpecUtils::IsSpec(forgeTypeId); // "spec" is a unit. Maybe ship those with out units? (What about text?)
                    //gltfVersion.extras.Add(paramDefinitionName + "_IsSpec", isSpec.ToString());

                    //auto humanLabel = LabelUtils::GetLabelForSpec(forgeTypeId);
                    //gltfVersion.extras.Add(paramDefinitionName + "_LabelUtils", humanLabel);

                    // useful?
                    // internalDefinition->GetParameterTypeId();        }
                }
            }

            // rootNode.Translation = new List<float> { 0, 0, 0 };
            rootNode.Children = new List<int>();
            nodes.AddOrUpdateCurrent("rootNode", rootNode);

            var defaultScene = new GltfScene();
            defaultScene.Nodes.Add(0);
            scenes.Add(defaultScene);

            extensionsUsed.Add("EXT_structural_metadata");
            extensions.Add("EXT_structural_metadata", extStructuralMetadata);

            Logger.Instance.Log("Finishing Start");

            return true;
        }

        public void Finish()
        {
            if (cancelation)
            {
                return;
            }

            FileExport.Run(bufferViews, buffers, binaryFileData,
                scenes, nodes, meshes, materials, accessors, extensionsUsed, extensions, gltfVersion, images, textures, samplers);
        }

        public bool IsCanceled()
        {
            return cancelation;
        }

        // Most instanced elements have this event stack order: OnElementBegin->OnInstanceBegin->(geometry/material events)->OnInstanceEnd->OnElementEnd
        // But some do this: OnElementBegin->OnInstanceBegin->->OnInstanceEnd->(geometry/material events)->OnElementEnd
        // This records if the latter has happened
        bool onInstanceEndCompleted = false;
        bool useTopStackTransform = false;
        public RenderNodeAction OnElementBegin(ElementId elementId)
        {
            onInstanceEndCompleted = false;
            useTopStackTransform = false;

            element = Doc.GetElement(elementId);

            // TODO: add actual user preferences
            var preferences = new Preferences();

            if (!Util.CanBeLockOrHidden(element, view) ||
                (element is Level))
            {
                skipElementFlag = true;
                return RenderNodeAction.Skip;
            }

            if (nodes.Contains(element.UniqueId))
            {
                // Duplicate element, skip adding.
                skipElementFlag = true;
                return RenderNodeAction.Skip;
            }

            if (element is RevitLinkInstance linkInstance)
            {
                linkTransformation = linkInstance.GetTransform();
            }

            Logger.Instance.Log("Starting OnElementBegin: " + element.Name);
            Logger.Instance.Log("nodes.CurrentKey: " + nodes.CurrentKey);

            if (element.Name == "Multistory Stairs")
            {
                System.Diagnostics.Debug.WriteLine("Multistory Stairs");
            }

            var newNode = new GltfNode();
            // TODO: needed?
            // GLTFExtras^ extras = gcnew GLTFExtras();
            // TODO: maybe useful utils for descriptions, names, etc
            //if (preferences->properties)
            //{
            //	newNode->name = Util::ElementDescription(element);
            //	extras->uniqueId = element->UniqueId;
            //	extras->parameters = Util::GetElementParameters(element, true);
            //	extras->elementCategory = element->Category->Name;
            //	extras->elementId = element->Id->IntegerValue;
            //}
            // newNode->extras = extras;

            // TODO: recheck if these are still needed
            var categoryName = element.Category != null ? element.Category.Name : "Undefined";
            var familyName = GetFamilyName(element);
            newNode.Name = Util.CreateClassName(categoryName, familyName) + ": " + GetTypeNameIfApplicable(elementId);
            newNode.Extensions.EXT_structural_metadata.Class = Util.GetGltfName(
                Util.CreateClassName(categoryName, familyName)
                );

            // TODO: maybe useful. For example "category" is an ElementId, so will come out as a stringified integer
            //newNode->extensions->EXT_structural_metadata->Properties->Add("elementId", elementId->IntegerValue);
            //newNode->extensions->EXT_structural_metadata->Properties->Add("category", categoryName);
            //newNode->extensions->EXT_structural_metadata->Properties->Add("familyName", familyName);
            //newNode->extensions->EXT_structural_metadata->Properties->Add("typeName", GetTypeNameIfApplicable(elementId));

            //newNode->extensions->EXT_structural_metadata->Properties->Add("testCategoryType", element->Category->CategoryType.ToString());
            //newNode->extensions->EXT_structural_metadata->Properties->Add("testBuiltInCategory", element->Category->BuiltInCategory);
            //newNode->extensions->EXT_structural_metadata->Properties->Add("testSubElementCount", element->GetSubelements()->Count);

            newNode.Extensions.EXT_structural_metadata.Properties.Add("uniqueId", element.UniqueId);
            newNode.Extensions.EXT_structural_metadata.Properties.Add("levelId", element.LevelId.IntegerValue.ToString());
            newNode.Extensions.EXT_structural_metadata.Properties.Add("categoryName", Util.GetGltfName(categoryName));


            // create a glTF property from any remaining Revit parameter not explicitly added above
            var parameterSet = element.Parameters;
            foreach (Parameter parameter in parameterSet)
            {
                string propertyName = Util.GetGltfName(parameter.Definition.Name);
                object paramValue = GetParameterValue(parameter);
                if (paramValue != null && !newNode.Extensions.EXT_structural_metadata.Properties.ContainsKey(propertyName))
                {
                    newNode.Extensions.EXT_structural_metadata.Properties.Add(propertyName, paramValue);
                }
            }

            extStructuralMetadata.AddCategory(categoryName);
            var classMetadata = extStructuralMetadata.AddFamily(categoryName, familyName);
            extStructuralMetadata.AddProperties(categoryName, familyName, parameterSet);
            nodes.AddOrUpdateCurrent(element.UniqueId, newNode);
            // set parent to Supercomponent if it exists
            if (element is FamilyInstance familyInstance && familyInstance.SuperComponent != null)
            {
                Element superComponent = familyInstance.SuperComponent;
                string superComponentClass = Util.GetGltfName(superComponent.Category.Name);
                classMetadata["parent"] = superComponentClass;
                GltfNode parentNode = nodes.GetElement(superComponent.UniqueId);
                if (parentNode.Children == null) parentNode.Children = new List<int>();
                parentNode.Children.Add(nodes.CurrentIndex);
                useTopStackTransform = true;
            } else
            {
                rootNode.Children.Add(nodes.CurrentIndex);
            }

            // Reset currentGeometry for new element
            if (currentGeometry == null)
            {
                currentGeometry = new IndexedDictionary<GeometryDataObject>();
            }
            else
            {
                currentGeometry.Reset();
            }

            if (currentVertices == null)
            {
                currentVertices = new IndexedDictionary<VertexLookupIntObject>();
            }
            else
            {
                currentVertices.Reset();
            }

            return RenderNodeAction.Proceed;
        }

        public void OnElementEnd(ElementId elementId)
        {
            Logger.Instance.Log("Starting OnElementEnd: " + element.Name);

            if (skipElementFlag ||
                currentVertices == null ||
                currentVertices.List.Count == 0 ||
                !Util.CanBeLockOrHidden(element, view))
            {
                skipElementFlag = false;
                return;
            }


            // create a new mesh for the node (assuming 1 mesh per node w/ multiple primitives on mesh)
            var newMesh = new GltfMesh
            {
                Name = element.Name,
                Primitives = new List<GltfMeshPrimitive>()
            };

            var collectionToHash = new Dictionary<string, GeometryDataObject>(); // contains data that will be common across instances
            // Add vertex data to currentGeometry for each geometry/material pairing
            foreach (KeyValuePair<string, VertexLookupIntObject> kvp in currentVertices.Dict)
            {
                GeometryDataObject geometryDataObject = currentGeometry.GetElement(kvp.Key);
                var vertices = geometryDataObject.Vertices;
                foreach (KeyValuePair<PointIntObject, int> p in kvp.Value)
                {
                    vertices.Add(p.Key.X);
                    vertices.Add(p.Key.Y);
                    vertices.Add(p.Key.Z);
                }

                var materialKey = kvp.Key.Split('_')[1];
                collectionToHash.Add(materialKey, geometryDataObject);

                // DEBUG
                //string serializedVertices = JsonConvert.SerializeObject(vertices, Formatting.Indented);
                //Logger.Instance.Log("Serialized Vertices:");
                //Logger.Instance.Log(serializedVertices);
            }

            // hash
            var geometryDataObjectHash = ComputeHash(collectionToHash);
            if (geometryDataObjectIndices.TryGetValue(geometryDataObjectHash, out var index))
            {
                nodes.CurrentItem.Mesh = index;
            }
            else
            {
                // Convert currentGeometry objects into glTFMeshPrimitives
                var preferences = new Preferences(); // TODO: use user-set preferences
                foreach (KeyValuePair<string, GeometryDataObject> kvp in currentGeometry.Dict)
                {
                    var name = kvp.Key;
                    var geometryDataObject = kvp.Value;
                    GltfBinaryData elementBinaryData = GltfExportUtils.AddGeometryMeta(
                        buffers,
                        accessors,
                        bufferViews,
                        geometryDataObject,
                        name,
                        elementId.IntegerValue,
                        preferences.Normals);

                    binaryFileData.Add(elementBinaryData);

                    var meshPrimitive = new GltfMeshPrimitive();
                    meshPrimitive.Attributes.POSITION = elementBinaryData.VertexAccessorIndex;
                    if (preferences.Normals)
                    {
                        meshPrimitive.Attributes.NORMAL = elementBinaryData.NormalsAccessorIndex;
                    }
                    if (elementBinaryData.TexCoordBuffer.Count > 0)
                    {
                        meshPrimitive.Attributes.TEXCOORD_0 = elementBinaryData.TexCoordAccessorIndex;
                    }
                    meshPrimitive.Indices = elementBinaryData.IndexAccessorIndex;
                    if (preferences.Materials)
                    {
                        var materialKey = kvp.Key.Split(UNDERSCORE)[1];
                        if (materials.Contains(materialKey))
                        {
                            // var material = materials.GetIndexFromUuid(materialKey);
                            var material = materials.Dict[materialKey];
                            if (material.Name != RevitMaterials.INVALID_MATERIAL)
                            {
                                meshPrimitive.Material = materials.GetIndexFromUuid(materialKey);
                            }
                        }
                    }

                    newMesh.Primitives.Add(meshPrimitive);
                }

                meshes.AddOrUpdateCurrent(element.UniqueId, newMesh);
                nodes.CurrentItem.Mesh = meshes.CurrentIndex;
                meshes.CurrentItem.Name = newMesh.Name;
                geometryDataObjectIndices.Add(geometryDataObjectHash, meshes.CurrentIndex);
            }


            //var meshHash = ComputeHash(newMesh);
            //if (meshHashIndices.TryGetValue(meshHash, out var index))
            //{
            //    nodes.CurrentItem.Mesh = index;
            //}
            //else
            //{
            //    meshes.AddOrUpdateCurrent(element.UniqueId, newMesh);
            //    nodes.CurrentItem.Mesh = meshes.CurrentIndex;
            //    meshes.CurrentItem.Name = newMesh.Name;
            //    meshHashIndices.Add(meshHash, meshes.CurrentIndex);
            //}

            Logger.Instance.Log("...Ended OnElementEnd: " + element.Name);
        }

        int instanceStackDepth = 0;
        Autodesk.Revit.DB.Transform topStackTransform = null;
        public RenderNodeAction OnInstanceBegin(InstanceNode instanceNode)
        {
            instanceStackDepth++;

            Logger.Instance.Log("Beginning OnInstanceBegin...");
            topStackTransform = instanceNode.GetTransform();
            Logger.Instance.Log("  topStackTransform: ");
            Logger.Instance.Log("  " + GetTransformDetails(topStackTransform));
            var transformationMultiply = CurrentTransform.Multiply(topStackTransform);
            Logger.Instance.Log("  transformationMultiply: ");
            Logger.Instance.Log("  " + GetTransformDetails(transformationMultiply));
            transformStack.Push(transformationMultiply);

            Logger.Instance.Log("...Ending OnInstanceBegin");
            return RenderNodeAction.Proceed;
        }

        string GetTransformDetails(Autodesk.Revit.DB.Transform transform)
        {
            var x = transform.BasisX;
            var y = transform.BasisY;
            var z = transform.BasisZ;
            var origin = transform.Origin;

            string transformDetails = $"BasisX: ({x.X}, {x.Y}, {x.Z})\n" +
                                      $"BasisY: ({y.X}, {y.Y}, {y.Z})\n" +
                                      $"BasisZ: ({z.X}, {z.Y}, {z.Z})\n" +
                                      $"Origin: ({origin.X}, {origin.Y}, {origin.Z})\n";

            return transformDetails;
        }


        Autodesk.Revit.DB.Transform cachedTransform;
        public void OnInstanceEnd(InstanceNode node)
        {
            Logger.Instance.Log("Beginning OnInstanceEnd");
            instanceStackDepth--;

            // Note: This method is invoked even for instances that were skipped.


            Autodesk.Revit.DB.Transform transform = transformStack.Pop();

            if (disableInstancing)
            {
                return;
            }

            // do not write to the node if there is an instance stack
            // this happens with railings because the balusters are sub-instances of the railing instance
            if (instanceStackDepth > 0) return;
            if (!transform.IsIdentity)
            {
                //Autodesk.Revit.DB.Transform justRotation = Autodesk.Revit.DB.Transform.Identity;
                //justRotation.BasisX = transform.BasisX.Normalize();
                //justRotation.BasisY = transform.BasisY.Normalize();
                //justRotation.BasisZ = transform.BasisZ.Normalize();

                //var justTranslation = Autodesk.Revit.DB.Transform.Identity;
                //justTranslation.Origin = new XYZ(transform.Origin.X, transform.Origin.Y, transform.Origin.Z);

                //var RT = justRotation * justTranslation;
                //var TR = justTranslation * justRotation;

                //Autodesk.Revit.DB.Transform finalTransform;
                //finalTransform = justTranslation;

                //Logger.Instance.Log("   adding transformation to glTF node matrix:");
                //var serializableTransform = new SerializableTransform(transform);
                //Logger.Instance.Log(serializableTransform.ToString());

                var currentNode = nodes.CurrentItem;
                //DEBUG
                if (currentNode.Name == "rootNode")
                {
                    Logger.Instance.Log("rootnode is in OnInstanceEnd");
                }

                topStackTransform = node.GetTransform();
                if (useTopStackTransform) // for nodes with a non-root parent
                {
                    if (!topStackTransform.IsIdentity)
                    {
                        Logger.Instance.Log("Writing topStackTransform:");
                        Logger.Instance.Log(GetTransformDetails(topStackTransform));
                        currentNode.Matrix = new List<double>
                        {
                            topStackTransform.BasisX.X, topStackTransform.BasisY.X, topStackTransform.BasisZ.X, 0.0,
                            topStackTransform.BasisX.Y, topStackTransform.BasisY.Y, topStackTransform.BasisZ.Y, 0.0,
                            topStackTransform.BasisX.Z, topStackTransform.BasisY.Z, topStackTransform.BasisZ.Z, 0.0,
                            topStackTransform.Origin.X, topStackTransform.Origin.Y, topStackTransform.Origin.Z, 1.0
                        };
                        }
                }
                else
                {
                    Logger.Instance.Log("Writing transform:");
                    Logger.Instance.Log(GetTransformDetails(transform));

                    currentNode.Matrix = new List<double>
                    {
                        transform.BasisX.X, transform.BasisY.X, transform.BasisZ.X, 0.0,
                        transform.BasisX.Y, transform.BasisY.Y, transform.BasisZ.Y, 0.0,
                        transform.BasisX.Z, transform.BasisY.Z, transform.BasisZ.Z, 0.0,
                        transform.Origin.X, transform.Origin.Y, transform.Origin.Z, 1.0
                    };
                }
                //currentNode.Matrix = new List<double>
                //{
                //    finalTransform.BasisX.X, finalTransform.BasisY.X, finalTransform.BasisZ.X, 0.0,
                //    finalTransform.BasisX.Y, finalTransform.BasisY.Y, finalTransform.BasisZ.Y, 0.0,
                //    finalTransform.BasisX.Z, finalTransform.BasisY.Z, finalTransform.BasisZ.Z, 0.0,
                //    finalTransform.Origin.X, finalTransform.Origin.Y, finalTransform.Origin.Z, 1.0
                //};

            }

            cachedTransform = transform;
            onInstanceEndCompleted = true;
        }

        public RenderNodeAction OnLinkBegin(LinkNode node)
        {
            Logger.Instance.Log("Beginning OnLinkBegin");
            isLink = true;

            documents.Add(node.GetDocument());

            transformStack.Push(CurrentTransform.Multiply(linkTransformation));
            LinkOriginalTranformation = new Autodesk.Revit.DB.Transform(CurrentTransform);

            // We can either skip this instance or proceed with rendering it.
            return RenderNodeAction.Proceed;
        }

        public void OnLinkEnd(LinkNode node)
        {
            isLink = false;
            // Note: This method is invoked even for instances that were skipped.
            transformStack.Pop();

            documents.RemoveAt(1); // remove the item added in OnLinkBegin
        }

        public RenderNodeAction OnFaceBegin(FaceNode node)
        {
            // do nothing: This custom exporter is not set to export faces.
            return RenderNodeAction.Proceed;
        }

        public void OnFaceEnd(FaceNode node)
        {
            // no-op: This custom exporter is not set to export faces.
        }

        public void OnRPC(RPCNode node)
        {
            Logger.Instance.Log("Beginning OnRPC...");
            // TODO: use user-defined prefs
            var preferences = new Preferences();

            List<Mesh> meshes = GeometryUtils.GetMeshes(Doc, element);

            if (meshes.Count == 0)
            {
                return;
            }

            foreach (var mesh in meshes)
            {
                int triangles = mesh.NumTriangles;
                if (triangles == 0)
                {
                    continue;
                }

                MaterialUtils.SetMaterial(Doc, preferences, mesh, materials, true);

                GltfExportUtils.AddOrUpdateCurrentItem(nodes, currentGeometry, currentVertices, materials);

                for (int i = 0; i < triangles; i++)
                {
                    MeshTriangle triangle = mesh.get_Triangle(i);
                    if (triangle == null)
                    {
                        continue;
                    }

                    var pts = new List<XYZ>
                    {
                        triangle.get_Vertex(0),
                        triangle.get_Vertex(1),
                        triangle.get_Vertex(2)
                    };

                    GltfExportUtils.AddVerticesAndFaces(currentVertices.CurrentItem, currentGeometry.CurrentItem, pts);

                    if (preferences.Normals)
                    {
                        GltfExportUtils.AddRPCNormals(preferences, triangle, currentGeometry.CurrentItem);
                    }
                }
            }
            Logger.Instance.Log("...Finishing OnRPC");
        }

        public void OnLight(LightNode node)
        {
            // this custom exporter is currently not exporting lights
        }

        bool khrTextureTransformAdded;
        public void OnMaterial(MaterialNode materialNode)
        {
            Logger.Instance.Log("Starting OnMaterial...");

            materialHasTexture = false;
            // TODO: user-defined parameters
            var preferences = new Preferences();
            if (preferences.Materials)
            {
                Export.RevitMaterials.Export(materialNode, Doc, materials, extStructuralMetadata, samplers, images, textures, ref materialHasTexture);

                var prefs = new Preferences();
                if (!prefs.Textures) materialHasTexture = false;

                if (!khrTextureTransformAdded && materialHasTexture)
                {
                    extensionsUsed.Add("KHR_texture_transform");
                    khrTextureTransformAdded = true;
                }
            }

            Logger.Instance.Log("...Ending OnMaterial: " + materialNode.NodeName);
        }

        public class SerializableTransform
        {
            public double[,] Matrix { get; set; }

            public SerializableTransform(Autodesk.Revit.DB.Transform transform)
            {
                Matrix = new double[4, 4] {
            { transform.BasisX.X, transform.BasisY.X, transform.BasisZ.X, 0 },
            { transform.BasisX.Y, transform.BasisY.Y, transform.BasisZ.Y, 0 },
            { transform.BasisX.Z, transform.BasisY.Z, transform.BasisZ.Z, 0 },
            { transform.Origin.X, transform.Origin.Y, transform.Origin.Z, 1 }
        };
            }

            public override string ToString()
            {
                var rows = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    var row = new List<string>();
                    for (int j = 0; j < 4; j++)
                    {
                        row.Add(Matrix[i, j].ToString());
                    }
                    rows.Add(string.Join(",", row));
                }
                return string.Join("\n", rows);
            }
        }

        public class SerializablePoint
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }

            public SerializablePoint(XYZ point)
            {
                X = point.X;
                Y = point.Y;
                Z = point.Z;
            }
        }

        bool disableInstancing = true;
        public void OnPolymesh(PolymeshTopology polymeshTopology)
        {
            Logger.Instance.Log("Starting OnPolymesh...");
            GltfExportUtils.AddOrUpdateCurrentItem(nodes, currentGeometry, currentVertices, materials);

            var pts = polymeshTopology.GetPoints();
            if (disableInstancing)
            {
                pts = pts.Select(p => CurrentTransform.OfPoint(p)).ToList();
            }
            else
            {
                // handle the case of where OnInstanceBegin and OnInstanceEnd occur with no events in between
                // i.e.: OnElementBegin->OnInstanceBegin->OnInstanceEnd->OnPolymesh
                if (onInstanceEndCompleted && instanceStackDepth == 0)
                {
                    var inverse = cachedTransform.Inverse;
                    pts = pts.Select(p => inverse.OfPoint(p)).ToList();
                }
                else if (instanceStackDepth == 2)
                {
                    pts = pts.Select(p => CurrentTransform.OfPoint(p)).ToList();
                }
            }
            //else // debug
            //{
            //    //pts = pts.Select(p => CurrentTransform.OfPoint(p)).ToList();
            //    //var serializableTransform = new SerializableTransform(CurrentTransform);
            //    ////Logger.Instance.Log("Serialized transform applied to points:\n" + serializableTransform.ToString());
            //    //Logger.Instance.Log("   Not applying any transformation to polymesh points");

            //    //var serializableTransform = new SerializableTransform(CurrentTransform);
            //    //Logger.Instance.Log("Serialized transform hypothetically applied to points:\n" + serializableTransform.ToString());
            //    //var transformedPts = pts.Select(p => CurrentTransform.OfPoint(p)).ToList();
            //    //var hypotheticalPoints = transformedPts.Select(p => new SerializablePoint(p)).ToList();
            //    //string hypotheticalPointsSerialized = JsonConvert.SerializeObject(hypotheticalPoints, Formatting.Indented);
            //    //Logger.Instance.Log("   polymeshTopology HYPOTHETICAL points going to the buffer:");
            //    Logger.Instance.Log(hypotheticalPointsSerialized);
            //}

            //var serializablePoints = pts.Select(p => new SerializablePoint(p)).ToList();
            //string serializedPoints = JsonConvert.SerializeObject(serializablePoints, Formatting.Indented);
            //Logger.Instance.Log("   polymeshTopology points going to the buffer:");
            //Logger.Instance.Log(serializedPoints);

            foreach (PolymeshFacet facet in polymeshTopology.GetFacets())
            {
                foreach (var vertIndex in facet.GetVertices())
                {
                    XYZ p = pts[vertIndex];
                    int vertexIndex = currentVertices.CurrentItem.AddVertex(new PointIntObject(p));
                    currentGeometry.CurrentItem.Faces.Add(vertexIndex);
                }
            }



            // TODO: use user-defined preferences
            var preferences = new Preferences();
            if (preferences.Normals)
            {
                GltfExportUtils.AddNormals(preferences, CurrentTransform, polymeshTopology, currentGeometry.CurrentItem.Normals);
            }

            if (materialHasTexture) GltfExportUtils.AddTexCoords(preferences, polymeshTopology, currentGeometry.CurrentItem.TexCoords);


            Logger.Instance.Log("...Ending OnPolymesh");
        }

        RenderNodeAction IExportContext.OnViewBegin(ViewNode node)
        {
            // do nothing
            return RenderNodeAction.Proceed;
        }

        void IExportContext.OnViewEnd(ElementId elementId)
        {
            // do nothing
        }

        bool skipElementFlag;
        bool isLink;
        Autodesk.Revit.DB.Transform LinkOriginalTranformation { get; set; }
        Autodesk.Revit.DB.Transform linkTransformation;
        IndexedDictionary<GeometryDataObject> currentGeometry;
        IndexedDictionary<VertexLookupIntObject> currentVertices;
        // Dictionary<string, int> meshHashIndices = new Dictionary<string, int>();
        Dictionary<string, int> geometryDataObjectIndices = new Dictionary<string, int>();
        bool materialHasTexture;

        string GetFamilyName(Element element)
        {
            if (element == null)
            {
                return "Invalid element";
            }

            if (element is FamilyInstance familyInstance)
            {
                var family = familyInstance.Symbol.Family;
                if (family != null)
                {
                    return family.Name;
                }
                else
                {
                    return "Family not found";
                }
            }

            else
            {
                return "System Family";
            }
        }

        string GetTypeNameIfApplicable(ElementId elementId)
        {
            var element = Doc.GetElement(elementId);
            if (element == null) return "Element not found";

            if (Doc.GetElement(element.GetTypeId()) is ElementType elementType)
            {
                return elementType.Name;
            }
            else
            {
                return "Type not applicable or not found";
            }
        }

        object GetParameterValue(Autodesk.Revit.DB.Parameter parameter)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Integer:
                    return parameter.AsInteger();
                case StorageType.Double:
                    return parameter.AsDouble();
                case StorageType.String:
                    return parameter.AsString();
                case StorageType.ElementId:
                    return parameter.AsElementId().IntegerValue.ToString();
                default:
                    return null;
            }
        }

        Autodesk.Revit.DB.Transform CurrentTransform
        {
            get
            {
                return transformStack.Peek();
            }
        }

        string ComputeHash(GltfMesh mesh)
        {
            var meshJson = JsonConvert.SerializeObject(mesh, Formatting.None, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(meshJson));

                // Convert byte array to a hex string
                var hash = new StringBuilder();
                foreach (var b in hashBytes)
                {
                    hash.Append(b.ToString("x2"));
                }
                return hash.ToString();
            }
        }

        public static string ComputeHash(Dictionary<string, GeometryDataObject> collectionToHash)
        {
            var settings = new JsonSerializerSettings
            {
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                Formatting = Formatting.None,
                NullValueHandling = NullValueHandling.Ignore
            };
            string json = JsonConvert.SerializeObject(collectionToHash, settings);

            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] data = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(json));

                // Convert the byte array to a hexadecimal string
                StringBuilder sBuilder = new StringBuilder();
                for (int i = 0; i < data.Length; i++)
                {
                    sBuilder.Append(data[i].ToString("x2"));
                }
                return sBuilder.ToString();
            }
        }
    }
}