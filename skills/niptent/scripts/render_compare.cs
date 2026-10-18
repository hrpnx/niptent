// 衣装の SHAPE を 0 と 100 にした画像を左右に並べて書き出す。素体の SHAPE は 100 にして撮る。
// 使い方: run_cs.py で設定を差し替えて実行する（SKILL.md 参照）。出力された PNG を開いて見る。
// スキンメッシュのまま SetBlendShapeWeight → Camera.Render すると変更が描画に乗らず、0 と 100 が同じ画像になる。
// そのため両方を BakeMesh した静的メッシュに置き換えて撮る。撮り終えたら元に戻す。

const string CLOTH = "";
const string BODY = "";       // 空なら、同じアバターの中で SHAPE を持つ最大のレンダラー
const string SHAPE = "Nipple_On";
const string OUT_DIR = "";    // 必ず Assets の外
const bool HIDE_OTHERS = true; // 他の衣装と、シーンの他のアバターを隠す（同じ位置に別のアバターが表示されていると写り込む）

if (CLOTH == "") return "set CLOTH";
if (OUT_DIR == "") return "set OUT_DIR (outside Assets)";
System.IO.Directory.CreateDirectory(OUT_DIR);
// GameObject.Find は非アクティブなオブジェクトを見つけないので、シーンのルートから辿る
System.Func<string, GameObject> find = path => {
  var parts = path.Split(new[] { '/' }, 2);
  foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
    if (r.name == parts[0]) { if (parts.Length == 1) return r; var t = r.transform.Find(parts[1]); if (t != null) return t.gameObject; }
  return null;
};
var clothGo = find(CLOTH);
if (clothGo == null) return "CLOTH not found: " + CLOTH;
var cloth = clothGo.GetComponent<SkinnedMeshRenderer>();
var root = clothGo.transform.root;
SkinnedMeshRenderer body = null;
if (BODY != "") body = find(BODY).GetComponent<SkinnedMeshRenderer>();
else foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
  if (r != cloth && r.sharedMesh != null && r.sharedMesh.GetBlendShapeIndex(SHAPE) >= 0 && (body == null || r.sharedMesh.vertexCount > body.sharedMesh.vertexCount)) body = r;
int ci = cloth.sharedMesh.GetBlendShapeIndex(SHAPE), bi = body.sharedMesh.GetBlendShapeIndex(SHAPE);
if (ci < 0) return "cloth has no " + SHAPE;
float cW = cloth.GetBlendShapeWeight(ci), bW = body.GetBlendShapeWeight(bi);

// SHAPE が動かす肌の中心を、アバターの左右（ローカル x の正負）ごとに求める
Mesh m0 = new Mesh(), m1 = new Mesh();
body.SetBlendShapeWeight(bi, 0); body.BakeMesh(m0, true); body.SetBlendShapeWeight(bi, 100); body.BakeMesh(m1, true);
var V0 = m0.vertices; var V1 = m1.vertices;
Vector3[] sum = { Vector3.zero, Vector3.zero }; float[] ws = { 0, 0 };
for (int i = 0; i < V0.Length; i++) {
  float d = (V1[i] - V0[i]).magnitude; if (d < 5e-5f) continue;
  var w = body.transform.TransformPoint(V1[i]); int side = root.InverseTransformPoint(w).x > 0 ? 0 : 1;
  sum[side] += w * d; ws[side] += d;
}
var centers = new System.Collections.Generic.List<KeyValuePair<string, Vector3>>();
if (ws[0] > 0) centers.Add(new KeyValuePair<string, Vector3>("plusX", sum[0] / ws[0]));
if (ws[1] > 0) centers.Add(new KeyValuePair<string, Vector3>("minusX", sum[1] / ws[1]));
var mid = Vector3.zero; foreach (var c in centers) mid += c.Value; mid /= centers.Count;

var views = new System.Collections.Generic.List<(string n, Vector3 p, Vector3 t, float fov)>();
views.Add(("front", mid + root.forward * 0.55f, mid, 22));
foreach (var c in centers) {
  float s = c.Key == "plusX" ? 1 : -1;
  views.Add(("close_" + c.Key, c.Value + root.forward * 0.4f + root.right * s * 0.12f + root.up * 0.03f, c.Value, 9));
  views.Add(("profile_" + c.Key, c.Value + root.right * s * 0.45f, c.Value, 5));
  views.Add(("top_" + c.Key, c.Value + root.up * 0.45f, c.Value, 5));
}

var off = new System.Collections.Generic.List<Renderer>();
foreach (var r in root.GetComponentsInChildren<Renderer>(false)) if (r.enabled && (HIDE_OTHERS || r == body || r == cloth)) { r.enabled = false; off.Add(r); }
if (HIDE_OTHERS) foreach (var sr in clothGo.scene.GetRootGameObjects()) if (sr.transform != root) foreach (var r in sr.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { r.enabled = false; off.Add(r); }
var temps = new System.Collections.Generic.List<UnityEngine.Object>();
System.Func<SkinnedMeshRenderer, GameObject> stat = smr => {
  var m = new Mesh(); smr.BakeMesh(m, true); temps.Add(m);
  var g = new GameObject("tmp_" + smr.name); g.transform.SetPositionAndRotation(smr.transform.position, smr.transform.rotation);
  g.transform.localScale = smr.transform.lossyScale; // BakeMesh の座標はレンダラーのローカル空間なので、fbx の拡縮 0.01 なども掛け直す
  g.AddComponent<MeshFilter>().sharedMesh = m; g.AddComponent<MeshRenderer>().sharedMaterials = smr.sharedMaterials; return g;
};
const int W = 900, H = 700;
var go = new GameObject("tmpcam"); var cam = go.AddComponent<Camera>();
cam.nearClipPlane = 0.01f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.25f, 0.25f, 0.28f);
var rt = new RenderTexture(W, H, 24); cam.targetTexture = rt;
var sb = new System.Text.StringBuilder();
GameObject bodyS = null, clothS = null;
try {
  body.SetBlendShapeWeight(bi, 100); bodyS = stat(body);
  var shots = new System.Collections.Generic.Dictionary<string, Texture2D[]>();
  for (int k = 0; k < 2; k++) {
    cloth.SetBlendShapeWeight(ci, k * 100); if (clothS) UnityEngine.Object.DestroyImmediate(clothS); clothS = stat(cloth);
    foreach (var v in views) {
      go.transform.position = v.p; go.transform.LookAt(v.t, v.n.StartsWith("top_") ? root.forward : root.up); cam.fieldOfView = v.fov; cam.nearClipPlane = v.n.StartsWith("top_") ? 0.36f : 0.01f; // 上からは頭を切り取る
      cam.Render(); RenderTexture.active = rt;
      var tex = new Texture2D(W, H, TextureFormat.RGB24, false); tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(); temps.Add(tex);
      if (!shots.ContainsKey(v.n)) shots[v.n] = new Texture2D[2]; shots[v.n][k] = tex;
    }
  }
  foreach (var kv in shots) {
    var outTex = new Texture2D(W * 2 + 8, H, TextureFormat.RGB24, false); temps.Add(outTex);
    var px = new Color[(W * 2 + 8) * H]; for (int i = 0; i < px.Length; i++) px[i] = Color.white; outTex.SetPixels(px);
    outTex.SetPixels(0, 0, W, H, kv.Value[0].GetPixels()); outTex.SetPixels(W + 8, 0, W, H, kv.Value[1].GetPixels()); outTex.Apply();
    var f = System.IO.Path.Combine(OUT_DIR, "cmp_" + cloth.name + "_" + kv.Key + ".png"); System.IO.File.WriteAllBytes(f, outTex.EncodeToPNG()); sb.AppendLine(f);
  }
  sb.AppendLine("left = cloth " + SHAPE + " 0, right = 100 (body " + SHAPE + " = 100)");
} finally {
  cloth.SetBlendShapeWeight(ci, cW); body.SetBlendShapeWeight(bi, bW);
  RenderTexture.active = null; cam.targetTexture = null; UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(go);
  if (bodyS) UnityEngine.Object.DestroyImmediate(bodyS); if (clothS) UnityEngine.Object.DestroyImmediate(clothS);
  foreach (var t in temps) if (t) UnityEngine.Object.DestroyImmediate(t);
  foreach (var r in off) r.enabled = true;
}
return sb.ToString();
