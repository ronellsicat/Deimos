using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;
using System.IO;
using System;
using System.Linq;
using UnityEngine.Events;
using UniRx;
using GeoJSON.Net.Geometry;
using Newtonsoft.Json;
using GeoJSON.Net.Feature;
using GeoJSON.Net;

namespace DxR
{
    /// <summary>
    /// This component can be attached to any GameObject (_parentObject) to create a
    /// data visualization. This component takes a JSON specification as input, and
    /// generates an interactive visualization as output. The JSON specification
    /// describes the visualization using ONE type of mark and one or more channels.
    /// </summary>
    public class Vis : MonoBehaviour
    {
        public string visSpecsURL = "example.json";                     // URL of vis specs; relative to specsRootPath directory.
        public bool enableGUI = true;                                   // Switch for in-situ GUI editor.
        public bool enableSpecsExpansion = false;                       // Switch for automatically replacing the vis specs text file on disk with inferrence result.
        public bool enableTooltip = true;                               // Switch for tooltip that shows datum attributes on-hover of mark instance.
        public bool verbose = true;                                     // Switch for verbose log.

        public static string UNDEFINED = "undefined";                   // Value used for undefined objects in the JSON vis specs.
        public static float SIZE_UNIT_SCALE_FACTOR = 1.0f / 1000.0f;    // Conversion factor to convert each Unity unit to 1 meter.
        public static float DEFAULT_VIS_DIMS = 500.0f;                  // Default dimensions of a visualization, if not specified.

        JSONNode visSpecs;                                              // Vis specs that is synced w/ the inferred vis specs and vis.
        JSONNode visSpecsInferred;                                      // This is the inferred vis specs and is ultimately used for construction.

        Parser parser = null;                                           // Parser of JSON specs and data in text format to JSONNode object specs.
        GUI gui = null;                                                 // GUI object (class) attached to GUI game object.
        GameObject tooltip = null;                                      // Tooltip game object for displaying datum info, e.g., on-hover.

        // Vis Properties:
        string title;                                                   // Title of vis displayed.
        float width;                                                    // Width of scene in millimeters.
        float height;                                                   // Heigh of scene in millimeters.
        float depth;                                                    // Depth of scene in millimeters.
        string markType;                                                // Type or name of mark used in vis.
        public Data data;                                               // Object containing data.
        bool IsLinked = false;                                          // Link to other vis object for interaction.
        string data_name=null;

        public List<GameObject> markInstances;                                 // List of mark instances; each mark instance corresponds to a datum.

        private GameObject parentObject = null;                         // Parent game object for all generated objects associated to vis.

        private GameObject viewParentObject = null;                     // Parent game object for all view related objects - axis, legend, marks.
        private GameObject marksParentObject = null;                    // Parent game object for all mark instances.

        private GameObject guidesParentObject = null;                   // Parent game object for all guies (axes/legends) instances.
        private GameObject interactionsParentObject = null;             // Parent game object for all interactions, e.g., filters.
        private GameObject markPrefab = null;                           // Prefab game object for instantiating marks.
        private List<ChannelEncoding> channelEncodings = null;          // List of channel encodings.

        List<string> marksList;                                         // List of mark prefabs that can be used at runtime.
        List<string> dataList;                                          // List of local data that can be used at runtime.

        private bool isReady = false;
        public bool IsReady { get { return isReady; } }

        private int frameCount = 0;
        public int FrameCount { get { return frameCount; } set { frameCount = value; } }

        private static readonly string[] supportedViewTransforms = new string[] { "aggregate", "bin", "density", "filter", "stack", "timeUnit" };
        private static readonly string[] supportedFieldTransforms = new string[] { "aggregate", "bin", "density", "filter", "stack", "timeUnit" };

        [Serializable]
        public class VisUpdatedEvent : UnityEvent<Vis, JSONNode> { }
        public VisUpdatedEvent VisUpdated;
        private JSONNode initialMorphSpecs;
        private JSONNode finalMorphSpecs;
        private bool isMorphing = false;
        private CompositeDisposable morphSubscriptions;

        private void Awake()
        {
            if (VisUpdated == null)
                VisUpdated = new VisUpdatedEvent();

            if (DxR.VisMorphs.MorphManager.Instance != null)
                DxR.VisMorphs.MorphManager.Instance.RegisterVisualisation(this);

            // Initialize objects:
            parentObject = gameObject;
            viewParentObject = gameObject.transform.Find("DxRView").gameObject;
            marksParentObject = viewParentObject.transform.Find("DxRMarks").gameObject;
            guidesParentObject = viewParentObject.transform.Find("DxRGuides").gameObject;
            interactionsParentObject = gameObject.transform.Find("DxRInteractions").gameObject;

            if (viewParentObject == null || marksParentObject == null)
            {
                throw new Exception("Unable to load DxRView and/or DxRMarks objects.");
            }

            parser = new Parser();

            // If there is no visSpecsURL defined, and a RuntimeInspectorVisSpecs component is also attached
            // to this GameObject, then we parse the JSON string specification contained in it instead
            if (visSpecsURL == "" && GetComponent<RuntimeInspectorVisSpecs>() != null)
            {
                string visSpecsString = GetComponent<RuntimeInspectorVisSpecs>().InputSpecification;
                parser.ParseString(visSpecsString, out visSpecs);
            }
            else
            {
                // Parse the vis specs URL into the vis specs object.
                parser.Parse(visSpecsURL, out visSpecs);
            }

            InitDataList();
            InitMarksList();

            // Initialize the GUI based on the initial vis specs.
            InitGUI();
            InitTooltip();

            // Update vis based on the vis specs.
            UpdateVis();
            isReady = true;
        }

        private void Update()
        {
            FrameCount++;
        }

        private void OnDestroy()
        {
            DxR.VisMorphs.MorphManager.Instance.DeregisterVisualisation(this);
        }

        public JSONNode GetVisSpecs()
        {
            return visSpecs;
        }

        public int GetNumMarkInstances()
        {
            return markInstances.Count;
        }

        public bool GetIsLinked()
        {
            return IsLinked;
        }

        private void InitTooltip()
        {
            GameObject tooltipPrefab = Resources.Load("Tooltip/Tooltip") as GameObject;
            if(tooltipPrefab != null)
            {
                tooltip = Instantiate(tooltipPrefab, parentObject.transform);
                tooltip.SetActive(false);
            }
        }

        /// <summary>
        /// Update the visualization based on the current visSpecs object (whether updated from GUI or text editor).
        /// Currently, deletes everything and reconstructs everything from scratch.
        /// TODO: Only reconstruct newly updated properties.
        /// </summary>
        private void UpdateVis(bool callUpdateEvent = true)
        {
            DeleteAllButMarks();

            UpdateVisConfig();

            UpdateVisData();

            UpdateMarkPrefab();

            InferVisSpecs();

            ConstructVis(visSpecsInferred);

            if (callUpdateEvent)
                VisUpdated.Invoke(this, GetVisSpecs());
        }

        private void ConstructVis(JSONNode specs)
        {
            CreateChannelEncodingObjects(specs);

            // New: Updates existing mark instances, or creates new ones if they do not yet exist
            UpdateMarkInstances();

            ApplyChannelEncodings();

            // Interactions need to be constructed
            // before axes and legends
            ConstructInteractions(specs);

            ConstructAxes(specs);

            ConstructLegends(specs);
        }

        #region Morph specific functions

        /// <summary>
        /// Update the visualisation based on the given newVisSpecs object and applies a morph (i.e., animated transition)
        /// using the given tweeningObservable stream. This stream should return a value between 0 and 1.
        /// </summary>
        public void ApplyVisMorph(JSONNode newVisSpecs, IObservable<float> tweeningObservable)
        {
            // TODO: For now, if there is any morph currently being applied, we ignore all further morph requests
            if (isMorphing)
                return;

            isMorphing = true;
            initialMorphSpecs = visSpecs;
            finalMorphSpecs = newVisSpecs;
            visSpecs = newVisSpecs;

            // Required parts of DxR (part of UpdateVis)
            UpdateVisConfig();
            UpdateVisData();
            UpdateMarkPrefab();
            InferVisSpecs();

            // Required parts of DxR (part of ConstructVis)
            CreateChannelEncodingObjects(visSpecsInferred);
            UpdateMarkInstances(false);  // NEW: Reuses existing mark instances rather than creating new ones from scratch each time
            ApplyMorphingChannelEncodings(tweeningObservable);
            ConstructInteractions(visSpecsInferred);
            ConstructAxes(visSpecsInferred);
            ConstructLegends(visSpecsInferred);
        }

        public void StopVisMorph(bool goToEnd)
        {
            foreach (GameObject mark in markInstances)
            {
                mark.GetComponent<Mark>().DisableMorphing();
            }

            if (goToEnd)
            {
                isMorphing = false;
                UpdateVisSpecsFromJSONNode(finalMorphSpecs); // This function already deletes all Marks
                initialMorphSpecs = null;
                finalMorphSpecs = null;
            }
            else
            {
                isMorphing = false;
                UpdateVisSpecsFromJSONNode(initialMorphSpecs);
                initialMorphSpecs = null;
                finalMorphSpecs = null;
            }

            // morphSubscriptions.Dispose();
        }

        /// <summary>
        /// Updates mark instances with new values if they already exist, or creates new ones if they
        /// either don't exist or do not match mark names
        /// </summary>
        private void UpdateMarkInstances(bool resetMarkValues = true)
        {
            if (markInstances == null) markInstances = new List<GameObject>();

            // Create one mark for each data point, creating marks if they do not exist
            for (int i = 0; i < data.values.Count; i++)
            {
                Dictionary<string, string> dataValue = data.values[i];

                GameObject markInstance;

                // If there is a mark instance for this dataValue, use it
                if (i < markInstances.Count)
                {
                    // But, only use it if its mark name is correct
                    // TODO: This is a hacky way of checking mark name, so it might break something later on
                    if (markInstances[i].name.Contains(markPrefab.name))
                    {
                        markInstance = markInstances[i];
                    }
                    else
                    {
                        // Otherwise, destroy the old mark and replace it with a new one
                        GameObject oldMarkInstance = markInstances[i];
                        markInstance = InstantiateMark(markPrefab, marksParentObject.transform);
                        markInstances[i] = markInstance;
                        Destroy(oldMarkInstance);
                    }
                }
                // If there isn't a mark instance for this dataValue, create one
                else
                {
                    markInstance = InstantiateMark(markPrefab, marksParentObject.transform);
                    markInstances.Add(markInstance);
                }


                // Copy datum in mark
                Mark mark = markInstance.GetComponent<Mark>();
                mark.datum = dataValue;

                // Rest mark values to default
                if (resetMarkValues)
                    mark.ResetToDefault();

                // Copy over polygons and centres for spatial data if applicable
                if (data.polygons != null)
                {
                    mark.polygons = data.polygons[i];
                    mark.centre = data.centres[i];
                }

                if (enableTooltip)
                {
                    mark.InitTooltip(ref tooltip);
                }
            }

            // Delete all leftover marks
            while (data.values.Count < markInstances.Count)
            {
                GameObject markInstance = markInstances[markInstances.Count - 1];
                Destroy(markInstance);
                markInstances.RemoveAt(markInstances.Count - 1);
            }
        }

        private void ApplyMorphingChannelEncodings(IObservable<float> tweeningObservable)
        {
            List<Mark> marks = markInstances.Select(go => go.GetComponent<Mark>()).ToList();

            foreach (Mark mark in marks)
            {
                // Before any channel encodings are applied, we make a call to the mark to store its starting geometric values
                mark.StoreInitialMarkValues();
                // // We then reset its geometric values to default in order to handle cases where channel encodings are removed
                mark.ResetToDefault();
            }

            // Apply channel encoding changes
            bool isDirectionChanged = false;
            foreach (ChannelEncoding ch in channelEncodings)
            {
                ApplyChannelEncoding(ch, ref markInstances);

                if(ch.channel == "xdirection" || ch.channel == "ydirection" || ch.channel == "zdirection")
                {
                    isDirectionChanged = true;
                }
            }

            if (isDirectionChanged)
            {
                foreach (Mark mark in marks)
                {
                    mark.SetRotation();
                }
            }

            // Now that all mark values have been set, store the final mark values and reload the initial state values
            // We will interpolate between the inital and final stored mark values
            foreach (Mark mark in marks)
            {
                mark.StoreFinalMarkValues();
                mark.LoadInitialMarkValues();

                // Lastly, we have the marks subscribe to the observable to interpolate between the initial and final mark values
                mark.InitialiseMorphing(tweeningObservable);
            }
        }

        #endregion // Morph specific functions

        private void ConstructInteractions(JSONNode specs)
        {
            if (specs["interaction"] == null) return;

            interactionsParentObject.GetComponent<Interactions>().Init(this);

            foreach (JSONObject interactionSpecs in specs["interaction"].AsArray)
            {
                if(interactionSpecs["type"] != null && interactionSpecs["field"] != null && interactionSpecs["domain"] != null)
                {
                    switch(interactionSpecs["type"].Value)
                    {
                        case "thresholdFilter":
                            AddThresholdFilterInteraction(interactionSpecs);
                            break;

                        case "toggleFilter":
                            AddToggleFilterInteraction(interactionSpecs);
                            break;

                        default:
                            return;
                    }

                    Debug.Log("Constructed interaction: " + interactionSpecs["type"].Value +
                        " for data field " + interactionSpecs["field"].Value);
                } else
                {
                    Debug.Log("Make sure interaction object has type, field, and domain specs.");
//                    throw new System.Exception("Make sure interaction object has type, field, and domain specs.");
                }

            }
        }

        private void AddThresholdFilterInteraction(JSONObject interactionSpecs)
        {
            if (interactionsParentObject != null)
            {
                interactionsParentObject.GetComponent<Interactions>().AddThresholdFilter(interactionSpecs);
            }
        }

        private void AddToggleFilterInteraction(JSONObject interactionSpecs)
        {
            if(interactionsParentObject != null)
            {
                interactionsParentObject.GetComponent<Interactions>().AddToggleFilter(interactionSpecs);
            }
        }

        private void ConstructLegends(JSONNode specs)
        {
            // Go through each channel and create legend for color, shape, or size channels:
            for (int channelIndex = 0; channelIndex < channelEncodings.Count; channelIndex++)
            {
                ChannelEncoding channelEncoding = channelEncodings[channelIndex];
                JSONNode legendSpecs = specs["encoding"][channelEncoding.channel]["legend"];
                if (legendSpecs != null && legendSpecs.Value.ToString() != "none" && channelEncoding.channel == "color")
                {
                    if (verbose)
                    {
                        Debug.Log("Constructing legend for channel " + channelEncoding.channel);
                    }

                    ConstructLegendObject(legendSpecs, ref channelEncoding);
                }
            }
        }

        private void ConstructLegendObject(JSONNode legendSpecs, ref ChannelEncoding channelEncoding)
        {
            GameObject legendPrefab = Resources.Load("Legend/Legend", typeof(GameObject)) as GameObject;
            if (legendPrefab != null && markPrefab != null)
            {
                channelEncoding.legend = Instantiate(legendPrefab, guidesParentObject.transform);
                channelEncoding.legend.GetComponent<Legend>().Init(interactionsParentObject.GetComponent<Interactions>());
                channelEncoding.legend.GetComponent<Legend>().UpdateSpecs(legendSpecs, ref channelEncoding, markPrefab);
            }
            else
            {
                throw new Exception("Cannot find legend prefab.");
            }
        }

        private void ConstructAxes(JSONNode specs)
        {
            // Go through each channel and create axis for each spatial / position channel:
            for (int channelIndex = 0; channelIndex < channelEncodings.Count; channelIndex++)
            {
                ChannelEncoding channelEncoding = channelEncodings[channelIndex];
                JSONNode axisSpecs = specs["encoding"][channelEncoding.channel]["axis"];
                if (axisSpecs != null && axisSpecs.Value.ToString() != "none" &&
                    (channelEncoding.channel == "x" || channelEncoding.channel == "y" ||
                    channelEncoding.channel == "z"))
                {
                    if (verbose)
                    {
                        Debug.Log("Constructing axis for channel " + channelEncoding.channel);
                    }

                    ConstructAxisObject(axisSpecs, ref channelEncoding);
                }
            }
        }

        private void ConstructAxisObject(JSONNode axisSpecs, ref ChannelEncoding channelEncoding)
        {
            GameObject axisPrefab = Resources.Load("Axis/Axis", typeof(GameObject)) as GameObject;
            if (axisPrefab != null)
            {
                channelEncoding.axis = Instantiate(axisPrefab, guidesParentObject.transform);
                channelEncoding.axis.GetComponent<Axis>().Init(interactionsParentObject.GetComponent<Interactions>(),
                    channelEncoding.field);
                channelEncoding.axis.GetComponent<Axis>().UpdateSpecs(axisSpecs, channelEncoding.scale);
            }
            else
            {
                throw new Exception("Cannot find axis prefab.");
            }
        }

        private void ApplyChannelEncodings()
        {
            bool isDirectionChanged = false;
            foreach(ChannelEncoding ch in channelEncodings)
            {
                ApplyChannelEncoding(ch, ref markInstances);

                if(ch.channel == "xdirection" || ch.channel == "ydirection" || ch.channel == "zdirection")
                {
                    isDirectionChanged = true;
                }
            }

            if(isDirectionChanged)
            {
                for (int i = 0; i < markInstances.Count; i++)
                {
                    Mark markComponent = markInstances[i].GetComponent<Mark>();

                    markComponent.SetRotation();
                }
            }
        }

        private void ApplyChannelEncoding(ChannelEncoding channelEncoding,
            ref List<GameObject> markInstances)
        {
            for(int i = 0; i < markInstances.Count; i++)
            {
                Mark markComponent = markInstances[i].GetComponent<Mark>();
                if (markComponent == null)
                {
                    throw new Exception("Mark component not present in mark prefab.");
                }

                if (channelEncoding.value != DxR.Vis.UNDEFINED)
                {
                    markComponent.SetChannelValue(channelEncoding.channel, channelEncoding.value);
                }
                else
                {
                    // Special condition for channel encodings with spatial data types
                    if (channelEncoding.fieldDataType.ToLower() == "spatial" &&
                        (channelEncoding.field.ToLower() == "longitude" ||
                         channelEncoding.field.ToLower() == "latitude"))
                    {
                        // The value that this mark receives will either be longitude or latitude
                        ((MarkGeoshape)markComponent).SetChannelEncoding(channelEncoding);
                        markComponent.SetChannelValue(channelEncoding.channel, channelEncoding.field.ToLower());
                    }
                    else
                    {
                        string channelValue = channelEncoding.scale.ApplyScale(markComponent.datum[channelEncoding.field]);
                        markComponent.SetChannelValue(channelEncoding.channel, channelValue);
                    }
                }
            }
        }

        private void ConstructMarkInstances()
        {
            markInstances = new List<GameObject>();

            // Create one mark prefab instance for each data point:
            int idx = 0;
            foreach (Dictionary<string, string> dataValue in data.values)
            {
                // Instantiate mark prefab, copying parentObject's transform:
                GameObject markInstance = InstantiateMark(markPrefab, marksParentObject.transform);

                // Copy datum in mark:
                Mark mark = markInstance.GetComponent<Mark>();
                mark.datum = dataValue;

                // Copy over polygons for spatial data if applicable
                if (data.polygons != null)
                {
                    mark.polygons = data.polygons[idx];
                    mark.centre = data.centres[idx];
                }

                // Assign tooltip:
                if(enableTooltip)
                {
                    mark.InitTooltip(ref tooltip);
                }

                markInstances.Add(markInstance);
                idx++;
            }
        }

        internal List<string> GetDataFieldsListFromURL(string dataURL)
        {
            return parser.GetDataFieldsList(dataURL);
        }

        private GameObject InstantiateMark(GameObject markPrefab, Transform parentTransform)
        {
            return Instantiate(markPrefab, parentTransform.position,
                        parentTransform.rotation, parentTransform);
        }

        private void CreateChannelEncodingObjects(JSONNode specs)
        {
            channelEncodings = new List<ChannelEncoding>();

            // Go through each channel and create ChannelEncoding for each:
            foreach (KeyValuePair<string, JSONNode> kvp in specs["encoding"].AsObject)
            {
                ChannelEncoding channelEncoding = new ChannelEncoding();

                channelEncoding.channel = kvp.Key;
                JSONNode channelSpecs = kvp.Value;
                if (channelSpecs["value"] != null)
                {
                    channelEncoding.value = channelSpecs["value"].Value.ToString();

                    if (channelSpecs["type"] != null)
                    {
                        channelEncoding.valueDataType = channelSpecs["type"].Value.ToString();
                    }
                }
                else
                {
                    channelEncoding.field = channelSpecs["field"];

                    // Check validity of data field
                    if(!data.fieldNames.Contains(channelEncoding.field))
                    {
                        throw new Exception("Cannot find data field " + channelEncoding.field + " in data. Please check your spelling (case sensitive).");
                    }

                    if (channelSpecs["type"] != null)
                    {
                        channelEncoding.fieldDataType = channelSpecs["type"];
                    }
                    else
                    {
                        throw new Exception("Missing type for field in channel " + channelEncoding.channel);
                    }
                }

                JSONNode scaleSpecs = channelSpecs["scale"];
                if (scaleSpecs != null)
                {
                    CreateScaleObject(scaleSpecs, ref channelEncoding.scale);
                }

                channelEncodings.Add(channelEncoding);
            }
        }

        private void CreateScaleObject(JSONNode scaleSpecs, ref Scale scale)
        {
            switch (scaleSpecs["type"].Value.ToString())
            {
                case "none":
                    scale = new ScaleNone(scaleSpecs);
                    break;

                case "linear":
                case "spatial":
                    scale = new ScaleLinear(scaleSpecs);
                    break;

                case "band":
                    scale = new ScaleBand(scaleSpecs);
                    break;

                case "point":
                    scale = new ScalePoint(scaleSpecs);
                    break;

                case "ordinal":
                    scale = new ScaleOrdinal(scaleSpecs);
                    break;

                case "sequential":
                    scale = new ScaleSequential(scaleSpecs);
                    break;

                default:
                    scale = null;
                    break;
            }
        }

        private void InferVisSpecs()
        {
            if (markPrefab != null)
            {
                markPrefab.GetComponent<Mark>().Infer(data, visSpecs, out visSpecsInferred, visSpecsURL);

                if(enableSpecsExpansion)
                {
                    JSONNode visSpecsToWrite = JSON.Parse(visSpecsInferred.ToString());
                    if (visSpecs["data"]["url"] != null && visSpecs["data"]["url"] != "inline")
                    {
                        visSpecsToWrite["data"].Remove("values");
                    }

                    if (visSpecs["interaction"].AsArray.Count == 0)
                    {
                        visSpecsToWrite.Remove("interaction");
                    }
#if UNITY_EDITOR
            System.IO.File.WriteAllText(Parser.GetFullSpecsPath(visSpecsURL), visSpecsToWrite.ToString(2));
#else
                    UnityEngine.Windows.File.WriteAllBytes(Parser.GetFullSpecsPath(visSpecsURL),
                        System.Text.Encoding.UTF8.GetBytes(visSpecsToWrite.ToString(2)));
#endif
                }
            }
            else
            {
                throw new Exception("Cannot perform inferrence without mark prefab loaded.");
            }
        }

        private void UpdateMarkPrefab()
        {
            string markType = visSpecs["mark"].Value;
            markPrefab = LoadMarkPrefab(markType);
        }

        private GameObject LoadMarkPrefab(string markName)
        {
            string markNameLowerCase = markName.ToLower();
            GameObject markPrefabResult = Resources.Load("Marks/" + markNameLowerCase + "/" + markNameLowerCase) as GameObject;

            if (markPrefabResult == null)
            {
                throw new Exception("Cannot load mark " + markNameLowerCase);
            }
            else if (verbose)
            {
                Debug.Log("Loaded mark " + markNameLowerCase);
            }

            // If the prefab does not have a Mark script attached to it, attach the default base Mark script object, i.e., core mark.
            if (markPrefabResult.GetComponent<Mark>() == null)
            {
                DxR.Mark markComponent = markPrefabResult.AddComponent(typeof(DxR.Mark)) as DxR.Mark;
            }
            markPrefabResult.GetComponent<Mark>().markName = markNameLowerCase;

            return markPrefabResult;
        }

        internal List<string> GetDataFieldsListFromValues(JSONNode valuesSpecs)
        {
            return parser.GetDataFieldsListFromValues(valuesSpecs);
        }

        private void UpdateVisData()
        {
            bool dataChanged = true;
            string dataString = "";
            JSONNode valuesSpecs = null;

            if(visSpecs["data"]["url"] != "inline")
            {
                string dataFilename = Parser.GetFullDataPath(visSpecs["data"]["url"].Value);
                dataString = Parser.GetStringFromFile(dataFilename);

                // Don't parse this data string if it is the same as the one that is currently used for the existing data
                // This is basically the same code from Parser.CreateValuesSpecs
                if (data != null && data.src == dataString)
                {
                    dataChanged = false;
                }
                else
                {
                    string ext = Path.GetExtension(dataFilename);
                    if (ext == ".json")
                    {
                        valuesSpecs = JSON.Parse(dataString);
                    }
                    else if (ext == ".csv")
                    {
                        valuesSpecs = JSON.ParseCSV(dataString);
                    }
                    else
                    {
                        throw new Exception("Cannot load file type" + ext);
                    }

                    visSpecs["data"].Add("values", valuesSpecs);
                    data_name = visSpecs["data"]["url"];
                }
            }
            else
            {
                valuesSpecs = visSpecs["data"]["values"];
                dataString = valuesSpecs.ToString();

                if (data != null && data.src == dataString)
                    dataChanged = false;
            }

            if (verbose)
                Debug.Log("Data update " + dataString);

            // Only update the vis data when it is either not yet defined, or if it has changed
            if (!dataChanged)
                return;

            data = new Data();
            data.src = dataString;

            // Data gets parsed differently depending if its in a standard or geoJSON format
            if (!IsDataGeoJSON(valuesSpecs))
            {
                PopulateTabularData(valuesSpecs, ref data);
            }
            else
            {
                PopulateGeoJSONData(valuesSpecs, ref data);
            }
        }

        private bool IsDataGeoJSON(JSONNode valuesSpecs)
        {
            if (valuesSpecs["type"] != null)
                return valuesSpecs["type"] == "FeatureCollection";

            return false;
        }

        private void PopulateTabularData(JSONNode valuesSpecs, ref Data data)
        {
            CreateDataFields(valuesSpecs, ref data);

            data.values = new List<Dictionary<string, string>>();

            int numDataFields = data.fieldNames.Count;
            if (verbose)
            {
                Debug.Log("Counted " + numDataFields.ToString() + " fields in data.");
            }

            // Loop through the values in the specification
            // and insert one Dictionary entry in the values list for each.
            foreach (JSONNode value in valuesSpecs.Children)
            {
                Dictionary<string, string> d = new Dictionary<string, string>();

                bool valueHasNullField = false;
                for (int fieldIndex = 0; fieldIndex < numDataFields; fieldIndex++)
                {
                    string curFieldName = data.fieldNames[fieldIndex];

                    // TODO: Handle null / missing values properly.
                    if (value[curFieldName].IsNull)
                    {
                        valueHasNullField = true;
                        Debug.Log("value null found: ");
                        break;
                    }

                    d.Add(curFieldName, value[curFieldName]);
                }

                if (!valueHasNullField)
                {
                    data.values.Add(d);
                }
            }

            if (visSpecs["data"]["linked"] != null)
            {
                if (visSpecs["data"]["linked"] == "true")
                {
                    IsLinked = true;
                }
            }
            //            SubsampleData(valuesSpecs, 8, "Assets/DxR/Resources/cars_subsampled.json");
        }

        private void PopulateGeoJSONData(JSONNode valuesSpecs, ref Data data)
        {
            data.values = new List<Dictionary<string, string>>();
            data.polygons = new List<List<List<IPosition>>>();
            data.centres = new List<IPosition>();

            // We have to use geoJSON.NET here
            var featureCollection = JsonConvert.DeserializeObject<FeatureCollection>(valuesSpecs.ToString());

            // Create the data fields for the geoJSON feature properties (similar to CreateDataFields())
            data.fieldNames = new List<string>();
            // We also add two here for Longitude and Latitude
            data.fieldNames.Add("Longitude");
            data.fieldNames.Add("Latitude");
            foreach (var kvp in featureCollection.Features.First().Properties)
            {
                data.fieldNames.Add(kvp.Key);
            }

            // Populate our data structures with those from the geoJSON
            foreach (var feature in featureCollection.Features)
            {
                // Add values (i.e., properties)
                Dictionary<string, string> d = new Dictionary<string, string>();
                foreach (var kvp in feature.Properties)
                {
                    d.Add(kvp.Key, kvp.Value.ToString());
                }
                data.values.Add(d);

                // Add polygons
                List<List<IPosition>> featurePolygons = new List<List<IPosition>>();
                switch (feature.Geometry.Type)
                {
                    case GeoJSONObjectType.Polygon:
                        {
                            var polygon = feature.Geometry as Polygon;
                            foreach (LineString lineString in polygon.Coordinates)
                            {
                                List<IPosition> positions = new List<IPosition>();

                                foreach (IPosition position in lineString.Coordinates)
                                {
                                    positions.Add(position);
                                }

                                // If the last position is the same as the first one, remove the last position
                                var firstPos = positions[0];
                                var lastPos = positions[positions.Count - 1];
                                if (firstPos.Latitude == lastPos.Latitude && firstPos.Longitude == lastPos.Longitude)
                                    positions.RemoveAt(positions.Count - 1);

                                featurePolygons.Add(positions);
                            }
                            break;
                        }

                    case GeoJSONObjectType.MultiPolygon:
                        {
                            MultiPolygon multiPolygon = feature.Geometry as MultiPolygon;
                            foreach (Polygon polygon in multiPolygon.Coordinates)
                            {
                                foreach (LineString lineString in polygon.Coordinates)
                                {
                                    List<IPosition> positions = new List<IPosition>();
                                    foreach (IPosition position in lineString.Coordinates)
                                    {
                                        positions.Add(position);
                                    }

                                    // If the last position is the same as the first one, remove the last position
                                    var firstPos = positions[0];
                                    var lastPos = positions[positions.Count - 1];
                                    if (firstPos.Latitude == lastPos.Latitude && firstPos.Longitude == lastPos.Longitude)
                                        positions.RemoveAt(positions.Count - 1);

                                    featurePolygons.Add(positions);
                                }
                            }
                            break;
                        }
                }

                data.polygons.Add(featurePolygons);

                // Find the centre position of these polygons as well
                // From https://stackoverflow.com/questions/6671183/calculate-the-center-point-of-multiple-latitude-longitude-coordinate-pairs
                var flattenedPositions = featurePolygons.SelectMany(x => x);
                double x = 0;
                double y = 0;
                double z = 0;

                foreach (var position in flattenedPositions)
                {
                    var latitude = position.Latitude * Math.PI / 180;
                    var longitude = position.Longitude * Math.PI / 180;

                    x += Math.Cos(latitude) * Math.Cos(longitude);
                    y += Math.Cos(latitude) * Math.Sin(longitude);
                    z += Math.Sin(latitude);
                }

                var total = flattenedPositions.Count();

                x = x / total;
                y = y / total;
                z = z / total;

                var centralLongitude = Math.Atan2(y, x);
                var centralSquareRoot = Math.Sqrt(x * x + y * y);
                var centralLatitude = Math.Atan2(z, centralSquareRoot);

                data.centres.Add(new Position(centralLatitude * 180 / Math.PI, centralLongitude * 180 / Math.PI));
            }
        }

        public string GetDataName()
        {
            return data_name;
        }

        private void SubsampleData(JSONNode data, int samplingRate, string outputName)
        {
            JSONArray output = new JSONArray();
            int counter = 0;
            foreach (JSONNode value in data.Children)
            {
                if (counter % 8 == 0)
                {
                    output.Add(value);
                }
                counter++;
            }

            System.IO.File.WriteAllText(outputName, output.ToString());
        }

        private JSONNode ApplyDataTransforms(JSONNode visSpecs, JSONNode valuesSpecs)
        {
            // // First, we check to see whether or not there are any data transforms to begin with
            // if (!CheckVisSpecsContainsTransforms(visSpecs))
            //     return valuesSpecs;

            // // We need to manually construct a Deedle data frame in order to perform data transformations easily
            // // We create a list of KVPs, where each element is a row in the data frame
            // // Each Series within it is another collection of KVPs, where each element is a named column in the data frame
            // var rows = new List<KeyValuePair<int, Deedle.Series<string, object>>>();

            // int numDataFields = valuesSpecs[0].AsObject.Count;

            // // Loop through the values in the specification (i.e., rows)
            // foreach (JSONNode value in valuesSpecs.Children)
            // {
            //     var sb = new Deedle.SeriesBuilder<string>();

            //     bool valueHasNullField = false;

            //     foreach (KeyValuePair<string, JSONNode> kvp in value.AsObject)
            //     {
            //         string curFieldName = kvp.Key;

            //         // TODO: Handle null / missing values properly.
            //         if (kvp.Value.IsNull)
            //         {
            //             valueHasNullField = true;
            //             Debug.Log("value null found: ");
            //             break;
            //         }

            //         sb.Add(curFieldName, kvp.Value);
            //     }

            //     if (!valueHasNullField)
            //     {
            //         rows.Add(Deedle.KeyValue.Create(rows.Count, sb.Series));
            //     }
            // }

            // var df = Deedle.Frame.FromRows(rows);

            // // We now apply data transforms to the data frame, starting with view-level transforms
            // // We resolve these in the same order that they are defined in the specification
            // foreach (JSONNode transformation in visSpecs["transform"].Children)
            // {
            //     ApplyViewLevelTransform(transformation, ref df);
            // }

            return null;

        }

        private bool CheckVisSpecsContainsTransforms(JSONNode visSpecs)
        {
            if (visSpecs["transform"] == null)
            {
                // Then we check if any of the encodings has a defined transform that is supported
                foreach (JSONNode encoding in visSpecs["encoding"].Children)
                {
                    foreach (KeyValuePair<string, JSONNode> kvp in encoding.AsObject)
                    {
                        if (supportedFieldTransforms.Contains(kvp.Key))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            return true;
        }

        // private void ApplyViewLevelTransform(JSONNode transformation, ref Frame<int, string> df)
        // {
        //     // Get the name of the transformation being used
        //     string name = GetViewLevelTransformName(transformation);

        //     switch (name)
        //     {
        //         case "filter":
        //             {
        //                 string expr = transformation.AsObject["filter"];
        //                 break;
        //             }
        //     }
        // }

        /// <summary>
        /// Gets the name of the transformation being applied. This should always be the first key in the provided JSONNode object
        /// </summary>
        private string GetViewLevelTransformName(JSONNode transformation)
        {
            foreach (KeyValuePair<string, JSONNode> kvp in transformation.AsObject)
            {
                return kvp.Key;
            }

            return "";
        }

        private void CreateDataFields(JSONNode valuesSpecs, ref Data data)
        {
            data.fieldNames = new List<string>();
            foreach (KeyValuePair<string, JSONNode> kvp in valuesSpecs[0].AsObject)
            {
                data.fieldNames.Add(kvp.Key);

                if (verbose)
                {
                    Debug.Log("Reading data field: " + kvp.Key);
                }
            }
        }

        private void UpdateVisConfig()
        {
            if (visSpecs["title"] != null)
            {
                title = visSpecs["title"].Value;
            }

            if (visSpecs["width"] == null)
            {
                visSpecs.Add("width", new JSONNumber(DEFAULT_VIS_DIMS));
                width = visSpecs["width"].AsFloat;
            } else
            {
                width = visSpecs["width"].AsFloat;
            }

            if (visSpecs["height"] == null)
            {
                visSpecs.Add("height", new JSONNumber(DEFAULT_VIS_DIMS));
                height = visSpecs["height"].AsFloat;
            }
            else
            {
                height = visSpecs["height"].AsFloat;
            }

            if (visSpecs["depth"] == null)
            {
                visSpecs.Add("depth", new JSONNumber(DEFAULT_VIS_DIMS));
                depth = visSpecs["depth"].AsFloat;
            }
            else
            {
                depth = visSpecs["depth"].AsFloat;
            }
        }

        private void DeleteAll()
        {
            foreach (Transform child in guidesParentObject.transform)
            {
                GameObject.Destroy(child.gameObject);
            }

            foreach (Transform child in marksParentObject.transform)
            {
                GameObject.Destroy(child.gameObject);
            }

            // TODO: Do not delete, but only update:
            foreach (Transform child in interactionsParentObject.transform)
            {
                GameObject.Destroy(child.gameObject);
            }
        }

        private void DeleteAllButMarks()
        {
            foreach (Transform child in guidesParentObject.transform)
            {
                GameObject.Destroy(child.gameObject);
            }

            // TODO: Do not delete, but only update:
            foreach (Transform child in interactionsParentObject.transform)
            {
                GameObject.Destroy(child.gameObject);
            }
        }

        private void DeleteMarks()
        {
            foreach (Transform child in marksParentObject.transform)
            {
                GameObject.Destroy(child.gameObject);
            }
        }

        private void InitGUI()
        {
            Transform guiTransform = parentObject.transform.Find("DxRGUI");
            GameObject guiObject = guiTransform.gameObject;
            gui = guiObject.GetComponent<GUI>();
            gui.Init(this);

            if (!enableGUI && guiObject != null)
            {
                guiObject.SetActive(false);
            }
        }

        private void UpdateGUISpecsFromVisSpecs()
        {
            gui.UpdateGUISpecsFromVisSpecs();
        }

        public void UpdateVisSpecsFromGUISpecs()
        {
            // For now, just reset the vis specs to empty and
            // copy the contents of the text to vis specs; starting
            // everything from scratch. Later on, the new specs will have
            // to be compared with the current specs to get a list of what
            // needs to be updated and only this list will be acted on.

            JSONNode guiSpecs = JSON.Parse(gui.GetGUIVisSpecs().ToString());


            // Remove data values so that parsing can put them again.
            // TODO: Optimize this.
            if (guiSpecs["data"]["url"] != null)
            {
                if(guiSpecs["data"]["url"] != "inline")
                {
                    guiSpecs["data"].Remove("values");
                    visSpecs["data"].Remove("values");

                    visSpecs["data"]["url"] = guiSpecs["data"]["url"];
                }
            }

            visSpecs["mark"] = guiSpecs["mark"];

            Debug.Log("GUI SPECS: " + guiSpecs.ToString());

            // UPDATE CHANNELS:

            // Go through vis specs and UPDATE fields and types of non-value channels
            // that are in the gui specs.
            List<string> channelsToUpdate = new List<string>();
            foreach (KeyValuePair<string, JSONNode> kvp in visSpecs["encoding"].AsObject)
            {
                string channelName = kvp.Key;
                if(visSpecs["encoding"][channelName]["value"] == null && guiSpecs["encoding"][channelName] != null)
                {
                    channelsToUpdate.Add(channelName);
                }
            }

            foreach(string channelName in channelsToUpdate)
            {
                visSpecs["encoding"][channelName]["field"] = guiSpecs["encoding"][channelName]["field"];
                visSpecs["encoding"][channelName]["type"] = guiSpecs["encoding"][channelName]["type"];
            }

            // Go through vis specs and DELETE non-field channels that are not in gui specs.
            List<string> channelsToDelete = new List<string>();
            foreach (KeyValuePair<string, JSONNode> kvp in visSpecs["encoding"].AsObject)
            {
                string channelName = kvp.Key;
                if (visSpecs["encoding"][channelName]["value"] == null && guiSpecs["encoding"][channelName] == null)
                {
                    channelsToDelete.Add(channelName);
                }
            }

            foreach (string channelName in channelsToDelete)
            {
                visSpecs["encoding"].Remove(channelName);
            }

            // Go through gui specs and ADD non-field channels in gui specs that are not in vis specs.
            foreach (KeyValuePair<string, JSONNode> kvp in guiSpecs["encoding"].AsObject)
            {
                string channelName = kvp.Key;
                Debug.Log("Testing channel " + channelName);

                if (guiSpecs["encoding"][channelName]["value"] == null && visSpecs["encoding"][channelName] == null)
                {
                    Debug.Log("Adding channel " + channelName);
                    visSpecs["encoding"].Add(channelName, guiSpecs["encoding"][channelName]);
                }
            }

            // UPDATE INTERACTIONS:
            // Go through vis specs and UPDATE fields and types of interactions
            // that are in the gui specs.
            List<string> fieldsToUpdate = new List<string>();
            foreach(JSONObject interactionSpecs in visSpecs["interaction"].AsArray)
            {
                string fieldName = interactionSpecs["field"];
                // If the field is in gui, it needs update:
                if(FieldIsInInteractionSpecs(guiSpecs["interaction"], fieldName))
                {
                    fieldsToUpdate.Add(fieldName);
                }
            }

            // Do the update:
            foreach (string fieldName in fieldsToUpdate)
            {
                visSpecs["interaction"][GetFieldIndexInInteractionSpecs(visSpecs["interaction"], fieldName)]["type"] =
                    guiSpecs["interaction"][GetFieldIndexInInteractionSpecs(visSpecs["interaction"], fieldName)]["type"];
            }

            // Go through vis specs and DELETE interactions for fields that are not in gui specs.
            List<string> fieldsToDelete = new List<string>();
            foreach (JSONObject interactionSpecs in visSpecs["interaction"].AsArray)
            {
                string fieldName = interactionSpecs["field"];
                if (!FieldIsInInteractionSpecs(guiSpecs["interaction"], fieldName))
                {
                    fieldsToDelete.Add(fieldName);
                }
            }

            foreach (string fieldName in fieldsToDelete)
            {
                visSpecs["interaction"].Remove(GetFieldIndexInInteractionSpecs(visSpecs["interaction"], fieldName));
            }

            // Go through gui specs and ADD interaction for fields in gui specs that are not in vis specs.
            foreach (JSONObject interactionSpecs in guiSpecs["interaction"].AsArray)
            {
                string fieldName = interactionSpecs["field"].Value;

                if (!FieldIsInInteractionSpecs(visSpecs["interaction"], fieldName))
                {
                    Debug.Log("Adding interaction for field " + fieldName);
                    visSpecs["interaction"].Add(guiSpecs["interaction"][GetFieldIndexInInteractionSpecs(guiSpecs["interaction"], fieldName)]);
                }
            }

            UpdateTextSpecsFromVisSpecs();
            UpdateVis();
        }

        private int GetFieldIndexInInteractionSpecs(JSONNode interactionSpecs, string searchFieldName)
        {
            int index = 0;
            foreach (JSONObject interactionObject in interactionSpecs.AsArray)
            {
                string fieldName = interactionObject["field"];
                if (fieldName == searchFieldName)
                {
                    return index;
                }
                index++;
            }
            return -1;
        }

        private bool FieldIsInInteractionSpecs(JSONNode interactionSpecs, string searchFieldName)
        {
            foreach (JSONObject interactionObject in interactionSpecs.AsArray)
            {
                string fieldName = interactionObject["field"];
                if(fieldName == searchFieldName)
                {
                    return true;
                }
            }
            return false;
        }

        private void InitDataList()
        {
            string[] dirs = Directory.GetFiles(Application.dataPath + "/StreamingAssets/DxRData");
            dataList = new List<string>();
            dataList.Add(DxR.Vis.UNDEFINED);
            dataList.Add("inline");
            for (int i = 0; i < dirs.Length; i++)
            {
                if (Path.GetExtension(dirs[i]) != ".meta")
                {
                    dataList.Add(Path.GetFileName(dirs[i]));
                }
            }
        }

        public List<string> GetDataList()
        {
            return dataList;
        }

        private void InitMarksList()
        {
            marksList = new List<string>();
            marksList.Add(DxR.Vis.UNDEFINED);

            TextAsset marksListTextAsset = (TextAsset)Resources.Load("Marks/marks", typeof(TextAsset));
            if (marksListTextAsset != null)
            {
                JSONNode marksListObject = JSON.Parse(marksListTextAsset.text);
                for (int i = 0; i < marksListObject["marks"].AsArray.Count; i++)
                {
                    string markNameLowerCase = marksListObject["marks"][i].Value.ToString().ToLower();
                    GameObject markPrefabResult = Resources.Load("Marks/" + markNameLowerCase + "/" + markNameLowerCase) as GameObject;

                    if (markPrefabResult != null)
                    {
                        marksList.Add(markNameLowerCase);
                    }
                }
            }
            else
            {
                throw new System.Exception("Cannot find marks.json file in Assets/DxR/Resources/Marks/ directory");
            }

#if UNITY_EDITOR
            string[] dirs = Directory.GetFiles("Assets/DxR/Resources/Marks");
            for (int i = 0; i < dirs.Length; i++)
            {
                if (Path.GetExtension(dirs[i]) != ".meta" && Path.GetExtension(dirs[i]) != ".json"
                    && !marksList.Contains(Path.GetFileName(dirs[i])))
                {
                    marksList.Add(Path.GetFileName(dirs[i]));
                }
            }
#endif

            if (!marksList.Contains(visSpecs["mark"].Value.ToString()))
            {
                marksList.Add(visSpecs["mark"].Value.ToString());
            }
        }

        public List<string> GetMarksList()
        {
            return marksList;
        }

        /// <summary>
        /// Manually calls an update to update the VisSpecs from a given string, rather than from a file URL
        /// </summary>
        public void UpdateVisSpecsFromStringSpecs(string specs)
        {
            JSONNode textSpecs;
            parser.ParseString(specs, out textSpecs);

            visSpecs = textSpecs;

            if (enableGUI)
                gui.UpdateGUISpecsFromVisSpecs();

            UpdateVis();
        }

        public void UpdateVisSpecsFromJSONNode(JSONNode specs, bool callUpdateEvent = true, bool updateGuiSpecs = true)
        {
            visSpecs = specs;

            if (enableGUI && updateGuiSpecs)
                gui.UpdateGUISpecsFromVisSpecs();

            UpdateVis(callUpdateEvent);
        }

        public void UpdateVisSpecsFromTextSpecs()
        {
            // For now, just reset the vis specs to empty and
            // copy the contents of the text to vis specs; starting
            // everything from scratch. Later on, the new specs will have
            // to be compared with the current specs to get a list of what
            // needs to be updated and only this list will be acted on.

            JSONNode textSpecs;
            parser.Parse(visSpecsURL, out textSpecs);

            visSpecs = textSpecs;

            if (enableGUI)
                gui.UpdateGUISpecsFromVisSpecs();

            UpdateVis();
        }

        public void UpdateTextSpecsFromVisSpecs()
        {
            JSONNode visSpecsToWrite = JSON.Parse(visSpecs.ToString());
            if(visSpecs["data"]["url"] != null && visSpecs["data"]["url"] != "inline")
            {
                visSpecsToWrite["data"].Remove("values");
            }

            if(visSpecs["interaction"].AsArray.Count == 0)
            {
                visSpecsToWrite.Remove("interaction");
            }

#if UNITY_EDITOR
            System.IO.File.WriteAllText(Parser.GetFullSpecsPath(visSpecsURL), visSpecsToWrite.ToString(2));
#else

            UnityEngine.Windows.File.WriteAllBytes(Parser.GetFullSpecsPath(visSpecsURL),
                System.Text.Encoding.UTF8.GetBytes(visSpecsToWrite.ToString(2)));
#endif
        }

        public List<string> GetChannelsList(string markName)
        {
            GameObject markObject = LoadMarkPrefab(markName);
            return markObject.GetComponent<Mark>().GetChannelsList();
        }

        public void Rescale(float scaleFactor)
        {
            viewParentObject.transform.localScale = Vector3.Scale(viewParentObject.transform.localScale,
                new Vector3(scaleFactor, scaleFactor, scaleFactor));
        }

        public void ResetView()
        {
            viewParentObject.transform.localScale = new Vector3(1, 1, 1);
            viewParentObject.transform.localEulerAngles = new Vector3(0, 0, 0);
            viewParentObject.transform.localPosition = new Vector3(0, 0, 0);
        }

        public void RotateAroundCenter(Vector3 rotationAxis, float angleDegrees)
        {
            Vector3 center = viewParentObject.transform.parent.transform.position +
                new Vector3(width * SIZE_UNIT_SCALE_FACTOR / 2.0f, height * SIZE_UNIT_SCALE_FACTOR / 2.0f,
                depth * SIZE_UNIT_SCALE_FACTOR / 2.0f);
            viewParentObject.transform.RotateAround(center, rotationAxis, angleDegrees);
        }

        // Update the visibility of each mark according to the filters results:
        internal void FiltersUpdated()
        {
            if(interactionsParentObject != null)
            {
                ShowAllMarks();

                foreach (KeyValuePair<string,List<bool>> filterResult in interactionsParentObject.GetComponent<Interactions>().filterResults)
                {
                    List<bool> visib = filterResult.Value;
                    for (int m = 0; m < markInstances.Count; m++)
                    {
                        markInstances[m].SetActive(visib[m] && markInstances[m].activeSelf);
                    }
                }
            }
        }

        void ShowAllMarks()
        {
            for (int m = 0; m < markInstances.Count; m++)
            {
                markInstances[m].SetActive(true);
            }
        }

        public Vector3 GetVisSize()
        {
            return new Vector3(width, height, depth);
        }
    }

}
