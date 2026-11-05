using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using CSLModernMap.Export;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CSLModernMap.Exporter
{
    /// <summary>Pure mapping of a captured city to the shared cmm-v1 contract.</summary>
    internal static class CmmMapper
    {
        public static JArray Vec(XMLVector3 p) { return new JArray(p.X, p.Y, p.Z); }
        private static JArray Xz(XMLVector3 p) { return new JArray(p.X, p.Z); }
        private static JArray Points(List<XMLVector3> points, bool planar)
        {
            JArray result = new JArray();
            foreach (XMLVector3 p in points) result.Add(planar ? Xz(p) : Vec(p));
            return result;
        }
        private static JObject Name(string name, string source)
        {
            return new JObject { { "text", name ?? "" }, { "source", string.IsNullOrEmpty(name) ? "empty" : source } };
        }
        private static string Mode(XMLNode n) { return n.UnderGround ? "TUNNEL" : n.OnGround ? "GROUND" : "ELEVATED"; }
        private static string Transit(string value)
        {
            string text = (value ?? "").Replace(" ", "").Replace("PublicTransport", "").ToUpperInvariant();
            if (text == "PLANE" || text == "BLIMP") return "AIRPLANE";
            string[] known = { "BUS", "TROLLEYBUS", "TRAM", "METRO", "TRAIN", "MONORAIL", "CABLECAR", "SHIP", "FERRY", "AIRPLANE", "HELICOPTER" };
            foreach (string kind in known) if (text == kind) return kind;
            return "OTHER";
        }
        private static string Classification(string value, string[] names, string[] terms)
        {
            string text = (value ?? "").ToLowerInvariant();
            for (int i = 0; i < terms.Length; i++)
                foreach (string term in terms[i].Split('|')) if (text.Contains(term)) return names[i];
            return "OTHER";
        }
        private static string BuildingRole(XMLBuilding b)
        {
            return Classification(b.Service + " " + b.SubService + " " + b.ItemClass,
                new[] { "AIRPORT", "TRANSIT_STATION", "EDUCATION", "HEALTHCARE", "EMERGENCY", "PARK_SERVICE", "RESIDENTIAL", "COMMERCIAL", "INDUSTRIAL", "OFFICE", "INFRASTRUCTURE", "LEISURE" },
                new[] { "airport|airplane|blimp", "publictransport|station|metro|train|tram|monorail", "school|university|education", "hospital|healthcare|clinic", "police|fire|emergency", "park|zoo|amusement", "residential|house|apartment", "commercial|shop", "industrial|industry|factory", "office", "power|water|sewage|sewer|garbage|transport", "leisure|stadium|park" });
        }
        private static string Zone(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "None") return "ORDINARY";
            return Classification(value,
                new[] { "FARMING", "FORESTRY", "ORE", "OIL", "HIGH_DENSITY", "RESIDENTIAL", "COMMERCIAL", "INDUSTRIAL", "OFFICE", "CAMPUS", "AIRPORT", "INDUSTRY", "ZOO", "AMUSEMENT", "NATURE_RESERVE", "PARK" },
                new[] { "farm", "forest", "ore", "oil", "highdensity|high density", "residential", "commercial", "industrial", "office|hightech|financial", "campus|university|liberalarts|tradeschool", "airport", "industry", "zoo", "amusement", "nature|reserve", "park|generic" });
        }
        private static JObject Grid(byte[] data)
        {
            string encoded = null;
            if (data != null)
                using (MemoryStream buffer = new MemoryStream())
                {
                    using (DeflaterOutputStream compressor = new DeflaterOutputStream(buffer))
                    { compressor.IsStreamOwner = false; compressor.Write(data, 0, data.Length); compressor.Finish(); }
                    encoded = Convert.ToBase64String(buffer.ToArray());
                }
            return new JObject { { "rows", 1081 }, { "cols", 1081 }, { "origin", new JArray(-8640, -8640) },
                { "cell", 17280.0 / 1081 }, { "axis", "rows=north;columns=east" },
                { "encoding", encoded == null ? null : "base64+zlib+float32-row-major" }, { "data", encoded } };
        }
        private static Dictionary<int, JArray> ZoneGrid(string text, out double cell)
        {
            Dictionary<int, JArray> result = new Dictionary<int, JArray>();
            cell = 0;
            if (string.IsNullOrEmpty(text)) return result;
            foreach (string item in text.Split(';'))
            {
                if (item.StartsWith("cell=")) { cell = double.Parse(item.Substring(5), CultureInfo.InvariantCulture); continue; }
                string[] values = item.Split(',');
                if (values.Length != 3) throw new InvalidDataException("Invalid grid entry: " + item);
                int id = int.Parse(values[2], CultureInfo.InvariantCulture);
                JArray points;
                if (!result.TryGetValue(id, out points)) { points = new JArray(); result.Add(id, points); }
                double x = double.Parse(values[0], CultureInfo.InvariantCulture), z = double.Parse(values[1], CultureInfo.InvariantCulture);
                if (double.IsNaN(x) || double.IsInfinity(x) || double.IsNaN(z) || double.IsInfinity(z))
                    throw new InvalidDataException("Non-finite grid position");
                points.Add(new JArray(x, z));
            }
            if (result.Count > 0 && (cell <= 0 || double.IsNaN(cell) || double.IsInfinity(cell)))
                throw new InvalidDataException("Missing or invalid grid cell size");
            return result;
        }
        private static Dictionary<int, JArray> ReadZoneGrid(string text, string context, JArray issues, out double cell)
        {
            try { return ZoneGrid(text, out cell); }
            catch (Exception ex)
            {
                if (!(ex is FormatException) && !(ex is OverflowException) && !(ex is InvalidDataException)) throw;
                issues.Add(Issue("INVALID_AREA_GRID", context + " boundaries omitted: " + ex.Message, null));
                cell = 0;
                return new Dictionary<int, JArray>();
            }
        }
        private static JObject District(int id, string name, string type, XMLVector3 position, Dictionary<int, JArray> grid, double cell, bool park)
        {
            JArray points;
            if (!grid.TryGetValue(id, out points)) points = new JArray();
            return new JObject { { "id", park ? -(id + 1) : id }, { "name", Name(name, "game") },
                { "kind", park ? "PARK_ZONE" : "DISTRICT" }, { "zone_kind", Zone(type) },
                { "grid_points", points }, { "grid_cell", cell }, { "position", position == null ? null : Xz(position) }, { "source_raw", type ?? "" } };
        }
        private static int? BuildingId(string text, HashSet<int> ids)
        {
            int value;
            return int.TryParse(text, out value) && value != 0 && ids.Contains(value) ? (int?)value : null;
        }
        public static JObject Map(MapSnapshot snapshot, ExportProgress progress = null)
        {
            ExportStages.Report(progress, 60, "EXPORT_PROGRESS_MAPPING");
            Dictionary<int, XMLNode> nodeMap = new Dictionary<int, XMLNode>();
            JArray nodes = new JArray(), networks = new JArray(), issues = new JArray();
            foreach (XMLNode n in snapshot.NodeList)
            {
                nodeMap.Add(n.Id, n);
                string station = Transit(n.SubService);
                nodes.Add(new JObject { { "id", n.Id }, { "pos", Vec(n.Position) }, { "mode_hint", Mode(n) },
                    { "station_service", station == "OTHER" ? JValue.CreateNull() : new JValue(station) }, { "source_raw", n.SubService ?? "" } });
            }
            Dictionary<int, JObject> networkMap = new Dictionary<int, JObject>();
            Dictionary<int, Dictionary<string, int>> trackIds = new Dictionary<int, Dictionary<string, int>>();
            JObject networkSources = new JObject();
            int mappedNetworks = 0;
            foreach (XMLSegment s in snapshot.SegmentList)
            {
                if ((mappedNetworks++ & 63) == 0)
                    ExportStages.Report(progress, 61 + 6 * mappedNetworks / snapshot.SegmentList.Count, "EXPORT_PROGRESS_MAPPING");
                NetworkFacts facts;
                if (!snapshot.Networks.TryGetValue(s.Id, out facts))
                { issues.Add(Issue("MISSING_NETWORK_FACTS", "Segment " + s.Id + " omitted: network facts missing.", s.Id)); continue; }
                if (!nodeMap.ContainsKey(s.StartNodeId) || !nodeMap.ContainsKey(s.EndNodeId))
                { issues.Add(Issue("MISSING_NETWORK_ENDPOINT", "Segment " + s.Id + " omitted: endpoint missing (" + s.StartNodeId + ", " + s.EndNodeId + ").", s.Id)); continue; }
                string level = facts.Highway ? "HIGHWAY" : facts.Kind == "BEAUTIFICATION" ? "BEAUTIFICATION" : facts.CarLaneCount >= 4 ? "MAIN" : "BRANCH";
                bool allowMissingDirection = facts.Kind == "ROAD";
                bool assumedDirection = allowMissingDirection && (!facts.CarForward.HasValue || !facts.CarBackward.HasValue);
                if (s.Name != null && (s.Name.ToLowerInvariant().Contains("airport") || s.Name.ToLowerInvariant().Contains("runway") || s.Name.ToLowerInvariant().Contains("taxiway"))) level = "SPECIAL";
                JObject record = new JObject { { "id", s.Id }, { "kind", facts.Kind }, { "start_id", s.StartNodeId }, { "end_id", s.EndNodeId },
                    { "geometry", Points(s.Points, false) }, { "mode", facts.Mode }, { "width", s.Width },
                    { "level", facts.Kind == "ROAD" || facts.Kind == "BEAUTIFICATION" ? level : null },
                    { "lane_summary", new JObject { { "car_lane_count", facts.CarLaneCount }, { "direction", facts.Direction } } },
                    { "car_access", new JObject { { "forward", facts.CarSpeedReadFailed ? "unknown" : (facts.CarForward ?? allowMissingDirection) ? "allowed" : "forbidden" },
                        { "backward", facts.CarSpeedReadFailed ? "unknown" : (facts.CarBackward ?? allowMissingDirection) ? "allowed" : "forbidden" },
                        { "source", facts.CarSpeedReadFailed ? "cs1-speed-read-failed-v1" : assumedDirection ? "cs1-default-allow-v1" : "cs1-native-lanes-v1" } } },
                    { "car_speed", new JObject {
                        { "forward", !facts.CarSpeedReadFailed && facts.CarForwardSpeedMps > 0 ? new JValue(facts.CarForwardSpeedMps) : JValue.CreateNull() },
                        { "backward", !facts.CarSpeedReadFailed && facts.CarBackwardSpeedMps > 0 ? new JValue(facts.CarBackwardSpeedMps) : JValue.CreateNull() },
                        { "unit", "m/s" }, { "source", "cs1-effective-car-lanes-v1" } } },
                    { "transit_for", facts.TransitFor }, { "path_segment_ids", new JArray() },
                    { "platform", (s.Name ?? "").ToLowerInvariant().Contains("platform") }, { "name", s.Name ?? "" },
                    { "display_name", s.CustomName ?? "" }, { "game_name", s.GameName ?? "" }, { "source_raw", s.ItemClass ?? "" } };
                if (facts.Footprint != null) record["footprint"] = Points(facts.Footprint, true);
                if (!facts.CarSpeedReadFailed && assumedDirection)
                    issues.Add(Issue("CAR_DIRECTION_ASSUMED", "Segment " + s.Id + " missing car direction; unknown directions allowed.", s.Id));
                networks.Add(record); networkMap.Add(s.Id, record);
                networkSources[s.Id.ToString(CultureInfo.InvariantCulture)] = new JObject { { "segment_id", s.Id }, { "invert", facts.Invert } };
                if (facts.Kind == "ROAD")
                {
                    Dictionary<string, int> derived = new Dictionary<string, int>(); trackIds.Add(s.Id, derived);
                    for (int t = 0; t < facts.Tracks.Count; t++)
                    {
                        int id = -(s.Id * 8 + t + 1);
                        JObject track = (JObject)record.DeepClone();
                        track["id"] = id; track["kind"] = facts.Tracks[t]; track["level"] = null;
                        track["game_name"] = "";
                        List<XMLVector3> geometry;
                        float width;
                        if (!facts.TrackGeometry.TryGetValue(facts.Tracks[t], out geometry) || !facts.TrackWidth.TryGetValue(facts.Tracks[t], out width))
                        { issues.Add(Issue("MISSING_TRACK_GEOMETRY", "Segment " + s.Id + " derived " + facts.Tracks[t] + " track omitted: lane geometry or width missing; road retained.", s.Id)); continue; }
                        track["geometry"] = Points(geometry, false); track["width"] = width;
                        track["lane_summary"] = null; track.Remove("footprint");
                        // Shared road/track strip is a source property. Independent ID
                        // keeps rail path references unambiguous without losing ROAD.
                        derived.Add(facts.Tracks[t], id); networks.Add(track); networkMap.Add(id, track);
                        networkSources[id.ToString(CultureInfo.InvariantCulture)] = new JObject { { "segment_id", s.Id }, { "derived_track", facts.Tracks[t] } };
                    }
                }
            }
            foreach (XMLSegment s in snapshot.SegmentList)
            {
                JObject record;
                if (!networkMap.TryGetValue(s.Id, out record)) continue;
                if ((string)record["kind"] != "STOP_LINE" || s.PathUnit == null) continue;
                JArray path = new JArray();
                string transit = (string)record["transit_for"];
                string pathKind = transit == "BUS" || transit == "TROLLEYBUS" ? "ROAD"
                    : transit == "TRAIN" ? "RAIL" : transit;
                string problem = null;
                foreach (int sourceId in s.PathUnit.SegmentIdList)
                {
                    int id = sourceId;
                    Dictionary<string, int> derived;
                    if (pathKind != "ROAD" && trackIds.TryGetValue(sourceId, out derived)
                        && !derived.TryGetValue(pathKind ?? "", out id))
                    { problem = "Segment " + sourceId + " has no derived " + pathKind + " track."; break; }
                    JObject target;
                    if (!networkMap.TryGetValue(id, out target))
                    { problem = "Missing segment " + sourceId + "; expected " + pathKind + "."; break; }
                    if ((string)target["kind"] != pathKind)
                    { problem = "Segment " + sourceId + " is " + (string)target["kind"] + "; expected " + pathKind + "."; break; }
                    if (path.Count == 0 || (int)path[path.Count - 1] != id) path.Add(id);
                }
                if (problem == null) record["path_segment_ids"] = path;
                else issues.Add(Issue("INVALID_TRANSIT_PATH", problem + " Path omitted; use stop-to-stop geometry.", s.Id));
            }
            foreach (string problem in snapshot.Problems) issues.Add(Issue("CS1_GEOMETRY", problem, null));
            ExportStages.Report(progress, 68, "EXPORT_PROGRESS_MAPPING");
            JArray buildings = new JArray(); HashSet<int> buildingIds = new HashSet<int>();
            foreach (XMLBuilding b in snapshot.BuildingList) buildingIds.Add(b.Id);
            HashSet<int> parkIds = new HashSet<int>();
            foreach (XMLDistrictPark p in snapshot.ParkList) parkIds.Add(p.Id);
            Dictionary<int, HashSet<int>> children = new Dictionary<int, HashSet<int>>();
            foreach (XMLBuilding b in snapshot.BuildingList)
            {
                int? parent = BuildingId(b.ParentBuildingId, buildingIds), child = BuildingId(b.SubBuildingId, buildingIds);
                if (parent.HasValue) AddChild(children, parent.Value, b.Id);
                if (child.HasValue) AddChild(children, b.Id, child.Value);
            }
            foreach (XMLBuilding b in snapshot.BuildingList)
            {
                JArray childIds = new JArray(); HashSet<int> childSet;
                if (children.TryGetValue(b.Id, out childSet)) foreach (int id in childSet) childIds.Add(id);
                string display = string.IsNullOrEmpty(b.MyName) ? b.DisplayName : b.MyName;
                int? areaId = b.ParkId != 0 && parkIds.Contains(b.ParkId) ? (int?)-(b.ParkId + 1) : null;
                if (b.ParkId != 0 && !areaId.HasValue)
                    issues.Add(Issue("MISSING_BUILDING_AREA", "Building " + b.Id + " area assignment omitted: park " + b.ParkId + " unavailable.", b.Id, "building"));
                if (string.IsNullOrEmpty(b.CategoryId) || string.IsNullOrEmpty(b.CategoryName) || string.IsNullOrEmpty(b.CategoryLocale))
                    issues.Add(Issue("MISSING_BUILDING_CATEGORY", "Building " + b.Id
                        + " has no complete official category localization; category label omitted.", b.Id, "building"));
                buildings.Add(new JObject { { "id", b.Id }, { "name", b.Name ?? "" }, { "display_name", Name(display, string.IsNullOrEmpty(b.MyName) ? "game" : "custom") },
                    { "category_id", b.CategoryId ?? "" }, { "category_name", b.CategoryName ?? "" }, { "category_locale", b.CategoryLocale ?? "" },
                    { "growth_kind", b.GrowthKind ?? "unknown" }, { "growth_use", b.GrowthUse ?? "" },
                    { "height_m", b.HeightMeters.HasValue ? new JValue(b.HeightMeters.Value) : JValue.CreateNull() },
                    { "base_elevation_m", b.BaseElevationMeters.HasValue ? new JValue(b.BaseElevationMeters.Value) : JValue.CreateNull() },
                    { "height_source", b.HeightMeters.HasValue ? "cs1:generated-bounds" : "" },
                    { "role", BuildingRole(b) }, { "footprint", Points(b.Points, true) }, { "parent_id", BuildingId(b.ParentBuildingId, buildingIds) },
                    { "child_ids", childIds }, { "area_id", areaId }, { "service", b.Service ?? "" }, { "sub_service", b.SubService ?? "" }, { "icls", b.ItemClass ?? "" },
                    { "sub_building_id", b.SubBuildingId ?? "" }, { "parent_building_id", b.ParentBuildingId ?? "" }, { "geometry_source", "cs1-building-bounds" } });
            }
            ExportStages.Report(progress, 71, "EXPORT_PROGRESS_MAPPING");
            JArray districts = new JArray(); double districtCell, parkCell;
            Dictionary<int, JArray> districtGrid = ReadZoneGrid(snapshot.DistrictGrid, "District", issues, out districtCell), parkGrid = ReadZoneGrid(snapshot.ParkGrid, "Park", issues, out parkCell);
            JObject districtSources = new JObject();
            foreach (XMLDistrict d in snapshot.DistrictList) districts.Add(District(d.Id, d.Name, d.Type, d.Position, districtGrid, districtCell, false));
            foreach (XMLDistrictPark p in snapshot.ParkList)
            {
                JObject area = District(p.Id, p.Name, p.Type, p.Position, parkGrid, parkCell, true);
                bool hasOwner = p.MainBuildingId != 0 && buildingIds.Contains(p.MainBuildingId);
                area["owner_building_id"] = hasOwner ? new JValue(p.MainBuildingId) : JValue.CreateNull();
                if (p.MainBuildingId != 0 && !hasOwner)
                    issues.Add(Issue("MISSING_AREA_OWNER", "Park " + p.Id + " owner assignment omitted: building " + p.MainBuildingId + " unavailable.", -(p.Id + 1), "district"));
                districts.Add(area);
                districtSources[(-(p.Id + 1)).ToString(CultureInfo.InvariantCulture)] = p.Id;
            }
            ExportStages.Report(progress, 74, "EXPORT_PROGRESS_MAPPING");
            JArray lines = new JArray(), stops = new JArray(), routes = new JArray();
            Dictionary<string, JObject> stopMap = new Dictionary<string, JObject>(); HashSet<int> stopIds = new HashSet<int>();
            foreach (XMLTransport line in snapshot.TransportList)
            {
                string kind = Transit(line.TransportType); XMLColor color = line.UserColor;
                lines.Add(new JObject { { "id", line.Id }, { "name", line.Name ?? "" }, { "kind", kind }, { "source_raw", line.TransportType ?? "" },
                    { "user_color", color == null || (color.R == 255 && color.G == 255 && color.B == 255 && color.A == 255) ? null : new JArray(color.R, color.G, color.B, color.A) } });
                JArray routeStops = new JArray();
                foreach (XMLStop source in line.Stops)
                {
                    XMLNode n;
                    if (!nodeMap.TryGetValue(source.Node, out n)) { issues.Add(Issue("MISSING_STOP_NODE", "Stop node omitted: " + source.Node, null)); continue; }
                    string stopKind = Transit(n.SubService); if (stopKind == "OTHER") stopKind = kind;
                    string key = n.Id + ":" + stopKind; JObject stop;
                    if (!stopMap.TryGetValue(key, out stop))
                    {
                        int id = n.Id; if (stopIds.Contains(id)) { id = n.Id * 100; while (stopIds.Contains(id)) id++; }
                        stopIds.Add(id);
                        stop = new JObject { { "id", id }, { "network_node_id", n.Id }, { "kind", stopKind },
                            { "name", Name("", "empty") }, { "position", Vec(n.Position) }, { "source_raw", n.SubService ?? "" }, { "serving_line_ids", new JArray() } };
                        stopMap.Add(key, stop); stops.Add(stop);
                    }
                    JArray serving = (JArray)stop["serving_line_ids"]; bool found = false;
                    foreach (JToken id in serving) if ((int)id == line.Id) found = true;
                    if (!found) serving.Add(line.Id);
                    routeStops.Add((int)stop["id"]);
                }
                bool closed = routeStops.Count > 1 && (int)routeStops[0] == (int)routeStops[routeStops.Count - 1];
                routes.Add(new JObject { { "id", line.Id * 2 + 1 }, { "line_id", line.Id }, { "direction", closed ? "LOOP" : "OUTBOUND" },
                    { "stop_ids", routeStops }, { "is_closed", closed }, { "geometry_kind", "STOP_DIRECT" }, { "path_segment_ids", new JArray() } });
            }
            ExportStages.Report(progress, 76, "EXPORT_PROGRESS_MAPPING");
            JArray forests = new JArray(), objects = new JArray();
            int forestRow = 0;
            foreach (string forest in snapshot.ForestList)
            {
                JArray data = new JArray(); bool valid = true;
                foreach (string value in forest.Split(',')) if (value.Length > 0)
                {
                    int parsed;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) { valid = false; break; }
                    data.Add(parsed);
                }
                // Preserve row indices: removing one row would shift all following forest cells.
                if (!valid)
                {
                    forests.Clear();
                    issues.Add(Issue("INVALID_FOREST_GRID", "Forests omitted: invalid data at row " + forestRow + ".", null));
                    break;
                }
                forests.Add(new JObject { { "data", data } }); forestRow++;
            }
            if (snapshot.ProceduralObjects != null) foreach (XMLProceduralObject p in snapshot.ProceduralObjects)
                objects.Add(new JObject { { "id", p.Id }, { "pos", new JArray(p.X, p.Y, p.Z) }, { "center_x", p.CenterX }, { "center_z", p.CenterZ },
                    { "extent_x", p.ExtentX }, { "extent_z", p.ExtentZ }, { "rotation_y", p.RotationY }, { "scale", p.Scale },
                    { "prefab_name", p.PrefabName ?? "" }, { "source_type", p.Source ?? "" },
                    { "color_rgb", p.ColorR == 255 && p.ColorG == 255 && p.ColorB == 255 ? null : new JArray(p.ColorR, p.ColorG, p.ColorB) } });
            JObject extensions = new JObject { { "cs1", new JObject { { "network_sources", networkSources }, { "park_source_ids", districtSources },
                { "simulation_paused", snapshot.SimulationPaused },
                { "centerline_tolerance_xz_m", 0.5 }, { "centerline_tolerance_y_m", 0.25 } } } };
            XMLMapTileGrid tiles = snapshot.MapTileGrid;
            if (tiles != null) extensions["map_tile_grid"] = new JObject { { "columns", tiles.Columns }, { "rows", tiles.Rows }, { "tile_size_m", tiles.TileSize }, { "origin", new JArray(tiles.OriginX, tiles.OriginZ) }, { "source", "game" } };
            ExportStages.Report(progress, 78, "EXPORT_PROGRESS_MAPPING");
            JObject document = new JObject { { "schema_version", "cmm-v1" },
                { "metadata", new JObject { { "origin", new JObject { { "game", "cs1" }, { "exporter", "CSLModernMap" }, { "source_name", snapshot.City } } },
                    { "exported_at", snapshot.Generated }, { "crs", "world-meters-y-up; rows=north" },
                    { "counters", new JObject { { "nodes", nodes.Count }, { "networks", networks.Count }, { "buildings", buildings.Count }, { "footprints", CountFootprints(networks) } } } } },
                { "sea_level", snapshot.SeaLevel }, { "terrain", new JObject { { "height", Grid(snapshot.TerrainHeight) }, { "water_depth", Grid(snapshot.TerrainWater) },
                    { "origin", new JArray(-8640, -8640) }, { "cell", 17280.0 / 1081 }, { "rows", 1081 }, { "cols", 1081 } } },
                { "nodes", nodes }, { "networks", networks }, { "buildings", buildings }, { "districts", districts },
                { "transit_lines", lines }, { "stops", stops }, { "routes", routes }, { "forests", forests }, { "procedural_objects", objects },
                { "derived", new JObject() }, { "issues", issues }, { "extensions", extensions } };
            if (snapshot.TerrainHeight == null && snapshot.TerrainWater == null)
            { document.Remove("terrain"); document.Remove("sea_level"); }
            if (!string.IsNullOrEmpty(snapshot.TrafficSnapshotJson))
            {
                try { document["traffic"] = JObject.Parse(snapshot.TrafficSnapshotJson); }
                catch (JsonException ex) { issues.Add(Issue("INVALID_TRAFFIC_SNAPSHOT", "Traffic snapshot omitted: " + ex.Message, null)); }
            }
            ExportStages.Report(progress, 80, "EXPORT_PROGRESS_WRITING");
            return document;
        }
        private static int CountFootprints(JArray networks)
        {
            int count = 0;
            foreach (JToken item in networks)
            {
                JObject record = (JObject)item;
                if (record["footprint"] != null) count++;
            }
            return count;
        }
        private static void AddChild(Dictionary<int, HashSet<int>> children, int parent, int child)
        { HashSet<int> ids; if (!children.TryGetValue(parent, out ids)) { ids = new HashSet<int>(); children.Add(parent, ids); } ids.Add(child); }
        private static JObject Issue(string code, string message, int? id, string kind = "network")
        { return new JObject { { "severity", "warn" }, { "code", code }, { "message", message }, { "refs", id.HasValue ? new JArray(new JObject { { "kind", kind }, { "id", id.Value } }) : new JArray() } }; }
    }
}
