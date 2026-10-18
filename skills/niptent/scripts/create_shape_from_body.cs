// 素体のシェイプキー（例: Nipple_On）が肌を押し出す分だけ、衣装のメッシュにも同名のシェイプキーを作る。
// 使い方: run_cs.py で設定を差し替えて実行する（SKILL.md 参照）。
//   1. APPLY = false で実行し、出力の before / after を読む。
//   2. 問題なければ APPLY = true で実行する。メッシュを OUT に書き出し、衣装のレンダラーを差し替え、
//      Modular Avatar が入っていれば Blendshape Sync を足す。シーンの保存は別に行う。
// 衣装のメッシュは複製して書き出すので、元の fbx / .asset は変えない。

const bool APPLY = false;
const string CLOTH = "";                // 衣装のレンダラー（ヒエラルキーのフルパス）
const string BODY = "";                 // 空なら、同じアバターの中で SHAPE を持つ最大のレンダラー
const string SHAPE = "Nipple_On";       // 素体のシェイプキー名。衣装にも同じ名前で作る
const string OUT = "";                  // 空なら「衣装のプレハブと同じフォルダ/<メッシュ名>_<SHAPE>.asset」
const bool OVERWRITE = false;           // 衣装にもう SHAPE がある時、作り直すか
const bool SYNC_ONLY = false;           // 衣装にもう SHAPE がある時、メッシュは変えず、値を素体に合わせて連動だけ足す
const bool ADD_SYNC = true;             // Modular Avatar の Blendshape Sync で素体の SHAPE に連動させる（MA が無ければ飛ばす）
const float TARGET = 0.0012f;           // 押し出し後に最低限あける肌との距離 (m)。トゥーンの輪郭線の幅より広くする
const float NEAR = 0.015f;              // 押し出しを判定する範囲: 動く肌からこの距離以内の衣装の頂点
const bool SUBDIVIDE = true;             // 乳首の周りの布を細かく割ってから形を作る（頂点数が元のメッシュと変わる）。割らないと、上から見た先の上面が短く、角が弱い
const float MAX_EDGE = 0.001f;          // 割った後の辺の長さの上限 (m)
const float SUB_RANGE = 0.004f;         // 割る範囲: 素体のキーが動かす肌からこの距離以内の布
const float PINCH_R = 0.024f;           // つまむ範囲: 乳首の軸からの距離 (m)。布は軸で D、PINCH_R で 0 になるよう (1 - q^2)^2 で減って出る
const float PINCH_CORE = 0.5f;          // つまむ量 D は、軸から PINCH_R * PINCH_CORE 以内の布が肌から離れる最小の値
const int PUSH_SMOOTH = 12;             // つまんだ後に足りない所を押し出した分を、1 回ごとにならす回数
const float HULL_R = 0.020f;            // 乳首の軸から HULL_R (m) 以内の布を、その範囲の凸包の面まで持ち上げる（0 でしない）
const float HULL_TOP = 0.004f;          // 先の上面: 軸から HULL_TOP (m) 以内の布を HULL_APEX 出した点を凸包に加える（先の平らな乳首に張った形）
const float HULL_APEX = 0.0015f;        // 先の上面を出す量 (m)。大きいほど先の角が立つ

var sb = new System.Text.StringBuilder();
// GameObject.Find は非アクティブなオブジェクトを見つけないので、シーンのルートから辿る
System.Func<string, GameObject> find = path => {
  var parts = path.Split(new[] { '/' }, 2);
  foreach (var r in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
    if (r.name == parts[0]) { if (parts.Length == 1) return r; var t = r.transform.Find(parts[1]); if (t != null) return t.gameObject; }
  return null;
};
if (CLOTH == "") return "set CLOTH (full hierarchy path of the cloth renderer)";
var clothGo = find(CLOTH);
if (clothGo == null) return "CLOTH not found: " + CLOTH;
var cloth = clothGo.GetComponent<SkinnedMeshRenderer>();
if (cloth == null) return "CLOTH has no SkinnedMeshRenderer";
var avatarRoot = clothGo.transform.root;
// EreMorph が編集データを持っていると、ビルド時に別のメッシュから作り直され、差し替えが効かない
for (var t = clothGo.transform; t != null; t = t.parent)
  foreach (var c in t.GetComponents<Component>())
    if (c != null && c.GetType().Name == "EreMorphVRChat") {
      var md = new UnityEditor.SerializedObject(c).FindProperty("_morphData");
      if (md != null && md.objectReferenceValue != null) return "EreMorph on " + t.name + " has morphData; this mesh is rebuilt at build time";
    }
SkinnedMeshRenderer body = null;
if (BODY != "") { var bg = find(BODY); if (bg != null) body = bg.GetComponent<SkinnedMeshRenderer>(); }
else foreach (var r in avatarRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
  if (r != cloth && r.sharedMesh != null && r.sharedMesh.GetBlendShapeIndex(SHAPE) >= 0 && (body == null || r.sharedMesh.vertexCount > body.sharedMesh.vertexCount)) body = r;
if (body == null) return "body with " + SHAPE + " not found";
sb.AppendLine("body=" + body.name + " cloth=" + cloth.name + " mesh=" + UnityEditor.AssetDatabase.GetAssetPath(cloth.sharedMesh));

var srcMesh = cloth.sharedMesh;
int existing = srcMesh.GetBlendShapeIndex(SHAPE);
if (existing >= 0 && APPLY && !OVERWRITE && !SYNC_ONLY) return "cloth already has " + SHAPE + ": read the existing: line of a dry run. If it covers, use SYNC_ONLY = true; to rebuild it, OVERWRITE = true";
float clothExistingW = existing >= 0 ? cloth.GetBlendShapeWeight(existing) : 0;

int ni = body.sharedMesh.GetBlendShapeIndex(SHAPE);
float orig = body.GetBlendShapeWeight(ni);
Mesh b1 = new Mesh(); body.SetBlendShapeWeight(ni, 100); body.BakeMesh(b1, true);
Mesh b0 = new Mesh(); body.SetBlendShapeWeight(ni, 0); body.BakeMesh(b0, true);
body.SetBlendShapeWeight(ni, orig);
var bt = body.transform;
var B0 = b0.vertices; var B1 = b1.vertices; var BN0 = b0.normals; var BN1 = b1.normals;
for (int i = 0; i < B0.Length; i++) {
  B0[i] = bt.TransformPoint(B0[i]); B1[i] = bt.TransformPoint(B1[i]);
  BN0[i] = bt.TransformDirection(BN0[i]); BN0[i] = BN0[i] / BN0[i].magnitude;
  BN1[i] = bt.TransformDirection(BN1[i]); BN1[i] = BN1[i] / BN1[i].magnitude;
}
var BT = body.sharedMesh.triangles;

// 素体で SHAPE が動かす範囲
var nip = new System.Collections.Generic.List<int>();
for (int i = 0; i < B0.Length; i++) if ((B1[i] - B0[i]).magnitude > 1e-6f) nip.Add(i);
if (nip.Count == 0) return "body " + SHAPE + " moves no vertex";
var big = new System.Collections.Generic.List<int>(); foreach (var i in nip) if ((B1[i] - B0[i]).magnitude > 5e-5f) big.Add(i);
Vector3 lo = Vector3.one * 1e9f, hi = -Vector3.one * 1e9f;
foreach (var i in nip) { lo = Vector3.Min(lo, B0[i]); hi = Vector3.Max(hi, B0[i]); }
float maxBody = 0; foreach (var i in nip) maxBody = Mathf.Max(maxBody, (B1[i] - B0[i]).magnitude);
lo -= Vector3.one * 0.03f; hi += Vector3.one * 0.03f;
System.Func<Vector3, bool> inBox = v => v.x > lo.x && v.y > lo.y && v.z > lo.z && v.x < hi.x && v.y < hi.y && v.z < hi.z;
sb.AppendLine("body moved verts=" + nip.Count + " max mm=" + (maxBody * 1000).ToString("F2"));

// 乳首の周りの布を細かく割る。布の輪郭が長い辺 1 本で乳首の先をまたぐと、上や横から見て先が平らな直線になる
var origMesh = srcMesh;
var keepNames = new string[origMesh.blendShapeCount]; var keepWeights = new float[origMesh.blendShapeCount];
for (int i = 0; i < keepNames.Length; i++) { keepNames[i] = origMesh.GetBlendShapeName(i); keepWeights[i] = cloth.GetBlendShapeWeight(i); }
System.Action<Mesh> useMesh = mm => { cloth.sharedMesh = mm; for (int i = 0; i < keepNames.Length; i++) { int j = mm.GetBlendShapeIndex(keepNames[i]); if (j >= 0) cloth.SetBlendShapeWeight(j, keepWeights[i]); } };
Mesh subMesh = null;
if (SUBDIVIDE && !SYNC_ONLY) {
  bool trisOnly = true; for (int sm = 0; sm < origMesh.subMeshCount; sm++) if (origMesh.GetTopology(sm) != MeshTopology.Triangles) trisOnly = false;
  if (trisOnly) {
    if (existing >= 0) cloth.SetBlendShapeWeight(existing, 0);
    var bk = new Mesh(); cloth.BakeMesh(bk, true);
    if (existing >= 0) cloth.SetBlendShapeWeight(existing, clothExistingW);
    var sW = new System.Collections.Generic.List<Vector3>(bk.vertices); for (int i = 0; i < sW.Count; i++) sW[i] = cloth.transform.TransformPoint(sW[i]);
    UnityEngine.Object.DestroyImmediate(bk);
    var sPts = new System.Collections.Generic.List<Vector3>(); foreach (var i in big) { sPts.Add(B0[i]); sPts.Add(B1[i]); }
    System.Func<Vector3, bool> sNear = q => { if (!inBox(q)) return false; foreach (var pt in sPts) if ((pt - q).sqrMagnitude < SUB_RANGE * SUB_RANGE) return true; return false; };
    var sReg = new System.Collections.Generic.List<bool>(); foreach (var q in sW) sReg.Add(sNear(q));
    var sV = new System.Collections.Generic.List<Vector3>(); origMesh.GetVertices(sV);
    var sN = new System.Collections.Generic.List<Vector3>(); origMesh.GetNormals(sN);
    var sT = new System.Collections.Generic.List<Vector4>(); origMesh.GetTangents(sT);
    var sC = new System.Collections.Generic.List<Color>(); origMesh.GetColors(sC);
    var sUV = new System.Collections.Generic.List<Vector4>[8];
    for (int ch = 0; ch < 8; ch++) if (origMesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0 + ch)) { sUV[ch] = new System.Collections.Generic.List<Vector4>(); origMesh.GetUVs(ch, sUV[ch]); }
    var sBW = new System.Collections.Generic.List<BoneWeight1[]>();
    { var bpv = origMesh.GetBonesPerVertex(); var all = origMesh.GetAllBoneWeights(); int k0 = 0;
      for (int i = 0; i < bpv.Length; i++) { var arr = new BoneWeight1[bpv[i]]; for (int k = 0; k < arr.Length; k++) arr[k] = all[k0 + k]; k0 += arr.Length; sBW.Add(arr); } }
    var sFrames = new System.Collections.Generic.List<System.Collections.Generic.List<Vector3>[]>();
    var sFrameKey = new System.Collections.Generic.List<int[]>();
    for (int sh = 0; sh < origMesh.blendShapeCount; sh++) for (int f = 0; f < origMesh.GetBlendShapeFrameCount(sh); f++) {
      var dv = new Vector3[sV.Count]; var dn = new Vector3[sV.Count]; var dt = new Vector3[sV.Count];
      origMesh.GetBlendShapeFrameVertices(sh, f, dv, dn, dt);
      sFrames.Add(new[] { new System.Collections.Generic.List<Vector3>(dv), new System.Collections.Generic.List<Vector3>(dn), new System.Collections.Generic.List<Vector3>(dt) });
      sFrameKey.Add(new[] { sh, f });
    }
    var sTris = new System.Collections.Generic.List<int>[origMesh.subMeshCount];
    for (int sm = 0; sm < origMesh.subMeshCount; sm++) sTris[sm] = new System.Collections.Generic.List<int>(origMesh.GetTriangles(sm));
    var sMid = new System.Collections.Generic.Dictionary<long, int>();
    System.Func<int, int, long> ek = (a, b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
    System.Func<int, int, int> midOf = (a, b) => {
      long kk = ek(a, b); int mv;
      if (sMid.TryGetValue(kk, out mv)) return mv;
      mv = sV.Count; sMid[kk] = mv;
      sV.Add((sV[a] + sV[b]) * 0.5f); sW.Add((sW[a] + sW[b]) * 0.5f); sReg.Add(sReg[a] || sReg[b]);
      if (sN.Count > 0) { var nn = sN[a] + sN[b]; sN.Add(nn.sqrMagnitude > 0 ? nn / nn.magnitude : sN[a]); }
      if (sT.Count > 0) { var ta = sT[a]; var tb = sT[b]; var tv = new Vector3(ta.x + tb.x, ta.y + tb.y, ta.z + tb.z); if (tv.sqrMagnitude > 0) tv = tv / tv.magnitude; sT.Add(new Vector4(tv.x, tv.y, tv.z, ta.w)); }
      if (sC.Count > 0) sC.Add((sC[a] + sC[b]) * 0.5f);
      for (int ch = 0; ch < 8; ch++) if (sUV[ch] != null) sUV[ch].Add((sUV[ch][a] + sUV[ch][b]) * 0.5f);
      var accW = new System.Collections.Generic.Dictionary<int, float>();
      foreach (var x in sBW[a]) accW[x.boneIndex] = (accW.ContainsKey(x.boneIndex) ? accW[x.boneIndex] : 0) + x.weight * 0.5f;
      foreach (var x in sBW[b]) accW[x.boneIndex] = (accW.ContainsKey(x.boneIndex) ? accW[x.boneIndex] : 0) + x.weight * 0.5f;
      var lst = new System.Collections.Generic.List<BoneWeight1>(); float tot = 0;
      foreach (var kv in accW) { var x = new BoneWeight1(); x.boneIndex = kv.Key; x.weight = kv.Value; lst.Add(x); tot += kv.Value; }
      lst.Sort((p1, p2) => p2.weight.CompareTo(p1.weight));
      for (int k = 0; k < lst.Count; k++) { var x = lst[k]; x.weight /= tot; lst[k] = x; }
      sBW.Add(lst.ToArray());
      foreach (var fr in sFrames) for (int c3 = 0; c3 < 3; c3++) fr[c3].Add((fr[c3][a] + fr[c3][b]) * 0.5f);
      return mv;
    };
    int sBefore = sV.Count;
    for (int round = 0; round < 8; round++) {
      var split = new System.Collections.Generic.HashSet<long>();
      for (int sm = 0; sm < sTris.Length; sm++) { var tl = sTris[sm];
        for (int q = 0; q < tl.Count; q += 3) for (int e = 0; e < 3; e++) { int a = tl[q + e], b = tl[q + (e + 1) % 3];
          if ((sReg[a] || sReg[b]) && (sW[a] - sW[b]).sqrMagnitude > MAX_EDGE * MAX_EDGE) split.Add(ek(a, b)); } }
      if (split.Count == 0) break;
      for (int sm = 0; sm < sTris.Length; sm++) {
        var tl = sTris[sm]; var nl = new System.Collections.Generic.List<int>(tl.Count * 2);
        for (int q = 0; q < tl.Count; q += 3) {
          int ta = tl[q], tb = tl[q + 1], tc = tl[q + 2];
          bool s0 = split.Contains(ek(ta, tb)), s1 = split.Contains(ek(tb, tc)), s2 = split.Contains(ek(tc, ta));
          int cnt = (s0 ? 1 : 0) + (s1 ? 1 : 0) + (s2 ? 1 : 0);
          if (cnt == 0) { nl.Add(ta); nl.Add(tb); nl.Add(tc); continue; }
          if (cnt == 3) { int q0 = midOf(ta, tb), q1 = midOf(tb, tc), q2 = midOf(tc, ta);
            nl.AddRange(new[] { ta, q0, q2, q0, tb, q1, q2, q1, tc, q0, q1, q2 }); continue; }
          // 向きを保ったまま回して、割る辺を x-y（1 本）または x-y と y-z（2 本）に揃える
          int x0 = ta, y0 = tb, z0 = tc;
          if (cnt == 1) { if (s1) { x0 = tb; y0 = tc; z0 = ta; } else if (s2) { x0 = tc; y0 = ta; z0 = tb; }
            int mm1 = midOf(x0, y0); nl.AddRange(new[] { x0, mm1, z0, mm1, y0, z0 }); continue; }
          if (!s2) { x0 = ta; y0 = tb; z0 = tc; } else if (!s0) { x0 = tb; y0 = tc; z0 = ta; } else { x0 = tc; y0 = ta; z0 = tb; }
          int mxy = midOf(x0, y0), myz = midOf(y0, z0);
          nl.AddRange(new[] { mxy, y0, myz, x0, mxy, myz, x0, myz, z0 });
        }
        sTris[sm] = nl;
      }
    }
    if (sV.Count > sBefore) {
      subMesh = new Mesh(); subMesh.name = origMesh.name;
      subMesh.indexFormat = sV.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : origMesh.indexFormat;
      subMesh.SetVertices(sV); if (sN.Count > 0) subMesh.SetNormals(sN); if (sT.Count > 0) subMesh.SetTangents(sT); if (sC.Count > 0) subMesh.SetColors(sC);
      for (int ch = 0; ch < 8; ch++) if (sUV[ch] != null) subMesh.SetUVs(ch, sUV[ch]);
      subMesh.subMeshCount = sTris.Length; for (int sm = 0; sm < sTris.Length; sm++) subMesh.SetTriangles(sTris[sm], sm, false);
      subMesh.bindposes = origMesh.bindposes;
      int totalW = 0; foreach (var arr in sBW) totalW += arr.Length;
      var nBpv = new Unity.Collections.NativeArray<byte>(sBW.Count, Unity.Collections.Allocator.Temp);
      var nAll = new Unity.Collections.NativeArray<BoneWeight1>(totalW, Unity.Collections.Allocator.Temp);
      int k1 = 0; for (int i = 0; i < sBW.Count; i++) { nBpv[i] = (byte)sBW[i].Length; foreach (var x in sBW[i]) nAll[k1++] = x; }
      subMesh.SetBoneWeights(nBpv, nAll); nBpv.Dispose(); nAll.Dispose();
      for (int fi = 0; fi < sFrames.Count; fi++) { int sh = sFrameKey[fi][0], f = sFrameKey[fi][1];
        subMesh.AddBlendShapeFrame(origMesh.GetBlendShapeName(sh), origMesh.GetBlendShapeFrameWeight(sh, f), sFrames[fi][0].ToArray(), sFrames[fi][1].ToArray(), sFrames[fi][2].ToArray()); }
      subMesh.RecalculateBounds();
      useMesh(subMesh); srcMesh = subMesh;
      sb.AppendLine("subdivided: verts " + sBefore + " -> " + sV.Count + " (edges longer than " + (MAX_EDGE * 1000).ToString("F1") + "mm near the moving skin)");
    } else sb.AppendLine("subdivided: nothing to split");
  }
}
try {
// 衣装は SHAPE を 0 にした形で焼く
if (existing >= 0) cloth.SetBlendShapeWeight(existing, 0);
Mesh cm = new Mesh(); cloth.BakeMesh(cm, true);
// 既存のキーを 100 にした形も焼いておき、そのキーで足りるかを測る
Mesh cmE = null;
if (existing >= 0) { cloth.SetBlendShapeWeight(existing, 100); cmE = new Mesh(); cloth.BakeMesh(cmE, true); cloth.SetBlendShapeWeight(existing, clothExistingW); }
var rt = cloth.transform;
var P = cm.vertices; var Nn = cm.normals; int nv = P.Length;
for (int i = 0; i < nv; i++) { P[i] = rt.TransformPoint(P[i]); Nn[i] = rt.TransformDirection(Nn[i]); Nn[i] = Nn[i] / Nn[i].magnitude; }
var RT = srcMesh.triangles;

// 三角形上の最近点（重心座標）
System.Func<Vector3, Vector3, Vector3, Vector3, Vector3> bary = (p, a, b, c) => {
  Vector3 ab = b - a, ac = c - a, ap = p - a;
  float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
  if (d1 <= 0 && d2 <= 0) return new Vector3(1, 0, 0);
  Vector3 bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
  if (d3 >= 0 && d4 <= d3) return new Vector3(0, 1, 0);
  float vc = d1 * d4 - d3 * d2;
  if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return new Vector3(1 - v, v, 0); }
  Vector3 cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
  if (d6 >= 0 && d5 <= d6) return new Vector3(0, 0, 1);
  float vb = d5 * d2 - d1 * d6;
  if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return new Vector3(1 - w, 0, w); }
  float va = d3 * d6 - d5 * d4;
  if (va <= 0 && (d4 - d3) >= 0 && (d5 - d6) >= 0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return new Vector3(0, 1 - w, w); }
  float den = 1f / (va + vb + vc); float vv = vb * den, ww = vc * den; return new Vector3(1 - vv - ww, vv, ww);
};

var btri = new System.Collections.Generic.List<int>();
for (int t = 0; t < BT.Length; t += 3) if (inBox(B0[BT[t]]) || inBox(B0[BT[t + 1]]) || inBox(B0[BT[t + 2]])) btri.Add(t);
// 近くの三角形だけを探すための格子（総当たりだと、細かく割った布で 30 秒を超える）
const float CELL = 0.004f;
System.Func<Vector3, Vector3Int> cellOf = v => new Vector3Int(Mathf.FloorToInt(v.x / CELL), Mathf.FloorToInt(v.y / CELL), Mathf.FloorToInt(v.z / CELL));
var gridB = new System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.List<int>>();
foreach (var t in btri) { var ck = cellOf((B0[BT[t]] + B0[BT[t + 1]] + B0[BT[t + 2]]) / 3f); System.Collections.Generic.List<int> cl; if (!gridB.TryGetValue(ck, out cl)) { cl = new System.Collections.Generic.List<int>(); gridB[ck] = cl; } cl.Add(t); }
// 中心から半径 r 以内に重心がある三角形を返す
System.Func<System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.List<int>>, Vector3[], int[], Vector3, float, System.Collections.Generic.List<int>> near3 = (grid, V, T, x, r) => {
  var res = new System.Collections.Generic.List<int>(); var c0c = cellOf(x); int n = Mathf.CeilToInt(r / CELL);
  for (int dx = -n; dx <= n; dx++) for (int dy = -n; dy <= n; dy++) for (int dz = -n; dz <= n; dz++) {
    System.Collections.Generic.List<int> cl; if (!grid.TryGetValue(new Vector3Int(c0c.x + dx, c0c.y + dy, c0c.z + dz), out cl)) continue;
    foreach (var t in cl) { var cen = (V[T[t]] + V[T[t + 1]] + V[T[t + 2]]) / 3f; if ((cen - x).sqrMagnitude < r * r) res.Add(t); }
  }
  return res;
};
var cand = new System.Collections.Generic.List<int>();
for (int i = 0; i < nv; i++) if (inBox(P[i])) cand.Add(i);
if (cand.Count == 0) return sb + "cloth has no vertex near the moved skin";

// 継ぎ目の頂点をまとめる（同じ位置の頂点は同じだけ動かす）と隣接
var key = new System.Collections.Generic.Dictionary<Vector3Int, int>();
var grp = new int[nv]; int ng = 0;
for (int i = 0; i < nv; i++) {
  var k = new Vector3Int(Mathf.RoundToInt(P[i].x * 1e5f), Mathf.RoundToInt(P[i].y * 1e5f), Mathf.RoundToInt(P[i].z * 1e5f));
  if (!key.TryGetValue(k, out var g)) { g = ng++; key[k] = g; } grp[i] = g;
}
var gRep = new int[ng]; for (int g = 0; g < ng; g++) gRep[g] = -1; for (int i = 0; i < nv; i++) if (gRep[grp[i]] < 0) gRep[grp[i]] = i;
var adj = new System.Collections.Generic.HashSet<int>[ng];
for (int g = 0; g < ng; g++) adj[g] = new System.Collections.Generic.HashSet<int>();
for (int t = 0; t < RT.Length; t += 3) for (int e = 0; e < 3; e++) { int a = grp[RT[t + e]], b = grp[RT[t + (e + 1) % 3]]; if (a != b) { adj[a].Add(b); adj[b].Add(a); } }
var active = new bool[ng]; foreach (var i in cand) active[grp[i]] = true;

// 衣装の頂点ごとに、近い動く肌の三角形
var actG = new System.Collections.Generic.List<int>();
var gTris = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
for (int g = 0; g < ng; g++) {
  if (!active[g]) continue; var x = P[gRep[g]]; bool nearN = false;
  foreach (var i in big) if ((B0[i] - x).sqrMagnitude < NEAR * NEAR) { nearN = true; break; }
  if (!nearN) continue;
  var l = near3(gridB, B0, BT, x, 0.008f);
  if (l.Count == 0) l = near3(gridB, B0, BT, x, NEAR + 0.005f);
  if (l.Count > 0) { actG.Add(g); gTris[g] = l; }
}
// 肌の面からの符号付き距離（外が正）。w に距離、xyz に面の法線
System.Func<Vector3, System.Collections.Generic.List<int>, Vector3[], Vector3[], Vector4> sdist = (x, tris, V, VN) => {
  float bd = 1e9f; Vector3 bq = Vector3.zero, bn = Vector3.up;
  foreach (var t in tris) { int a = BT[t], b = BT[t + 1], c = BT[t + 2]; var w = bary(x, V[a], V[b], V[c]); var q = V[a] * w.x + V[b] * w.y + V[c] * w.z; float d = (q - x).sqrMagnitude; if (d < bd) { bd = d; bq = q; bn = VN[a] * w.x + VN[b] * w.y + VN[c] * w.z; } }
  bn = bn / bn.magnitude; return new Vector4(bn.x, bn.y, bn.z, Vector3.Dot(x - bq, bn));
};
// 肌の側から見た突き抜け: 動く肌の頂点ごとに、最寄りの衣装の三角形の上の点と、その点の法線方向の距離を測る。
// 頂点だけで測ると、頂点の間の面から抜ける乳首の先を見逃す。
// 衣装が COVER より遠い肌は、衣装に覆われていないとみなして数えない
const float COVER = 0.006f;
var ctri = new System.Collections.Generic.List<int>();
for (int t = 0; t < RT.Length; t += 3) if (inBox(P[RT[t]]) || inBox(P[RT[t + 1]]) || inBox(P[RT[t + 2]])) ctri.Add(t);
var gridC = new System.Collections.Generic.Dictionary<Vector3Int, System.Collections.Generic.List<int>>();
foreach (var t in ctri) { var ck = cellOf((P[RT[t]] + P[RT[t + 1]] + P[RT[t + 2]]) / 3f); System.Collections.Generic.List<int> cl; if (!gridC.TryGetValue(ck, out cl)) { cl = new System.Collections.Generic.List<int>(); gridC[ck] = cl; } cl.Add(t); }
var skinTris = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
foreach (var bi in big) {
  var l = near3(gridC, P, RT, B1[bi], 0.01f);
  var seen = new System.Collections.Generic.HashSet<int>(l);
  foreach (var t in near3(gridC, P, RT, B0[bi], 0.01f)) if (seen.Add(t)) l.Add(t);
  skinTris[bi] = l;
}
// 戻り値: x = 距離（外が正）、y = 最寄り点までの距離、z = 三角形の先頭の添字（無ければ -1）。法線は nOut に返す
System.Func<Vector3, int, Vector3[], Vector3[], Vector3> tipDist = (x, bi, GD0, nOut) => {
  float bd = 1e9f; int bt3 = -1; Vector3 bw = Vector3.zero;
  foreach (var t in skinTris[bi]) {
    int a = RT[t], b = RT[t + 1], c = RT[t + 2];
    Vector3 pa = P[a] + GD0[grp[a]], pb = P[b] + GD0[grp[b]], pc = P[c] + GD0[grp[c]];
    var w = bary(x, pa, pb, pc); float d = (pa * w.x + pb * w.y + pc * w.z - x).sqrMagnitude;
    if (d < bd) { bd = d; bt3 = t; bw = w; }
  }
  if (bt3 < 0) return new Vector3(0, 1e9f, -1);
  int A = RT[bt3], B = RT[bt3 + 1], C = RT[bt3 + 2];
  var n = Nn[A] * bw.x + Nn[B] * bw.y + Nn[C] * bw.z; n = n / n.magnitude; nOut[0] = n;
  var q = (P[A] + GD0[grp[A]]) * bw.x + (P[B] + GD0[grp[B]]) * bw.y + (P[C] + GD0[grp[C]]) * bw.z;
  return new Vector3(Vector3.Dot(q - x, n), Mathf.Sqrt(bd), bt3);
};
var nTmp = new Vector3[1];
System.Func<Vector3[], Vector3[], string> skinCheck = (skin, GD0) => {
  int pen = 0, covered = 0; float worst = 1e9f;
  foreach (var bi in big) {
    var r = tipDist(skin[bi], bi, GD0, nTmp);
    if (r.z < 0 || r.y > COVER) continue; covered++;
    if (r.x < 0) pen++; worst = Mathf.Min(worst, r.x);
  }
  return "covered=" + covered + "/" + big.Count + " penetrating=" + pen + (covered > 0 ? " min mm=" + (worst * 1000).ToString("F2") : "");
};
var zero = new Vector3[ng];
sb.AppendLine("cloth verts checked=" + actG.Count);
sb.AppendLine("before: " + SHAPE + "=0 " + skinCheck(B0, zero));
sb.AppendLine("before: " + SHAPE + "=100 " + skinCheck(B1, zero));
if (cmE != null) {
  var PE = cmE.vertices; var GE = new Vector3[ng];
  for (int g = 0; g < ng; g++) GE[g] = rt.TransformPoint(PE[gRep[g]]) - P[gRep[g]];
  sb.AppendLine("existing: cloth " + SHAPE + "=100 " + skinCheck(B1, GE) + " (the key the cloth already has)");
}

// 布の頂点ごとに、真下の肌が素体のキーでどれだけ動くか（最大に対する割合）
var moveFrac = new System.Collections.Generic.Dictionary<int, float>();
foreach (var g in actG) {
  var x = P[gRep[g]]; float bd = 1e9f; float dmag = 0;
  foreach (var t in gTris[g]) { int a = BT[t], b = BT[t + 1], c = BT[t + 2]; var w = bary(x, B0[a], B0[b], B0[c]); var q = B0[a] * w.x + B0[b] * w.y + B0[c] * w.z; float d = (q - x).sqrMagnitude;
    if (d < bd) { bd = d; dmag = ((B1[a] - B0[a]) * w.x + (B1[b] - B0[b]) * w.y + (B1[c] - B0[c]) * w.z).magnitude; } }
  moveFrac[g] = dmag / maxBody;
}
// 肌との距離は元の距離を保つ。TARGET（輪郭線のはみ出し対策）は乳首の近く（真下の肌が最大の 1/4 以上動く所）でだけ使う
var c0 = new System.Collections.Generic.Dictionary<int, float>();
foreach (var g in actG) c0[g] = Mathf.Max(sdist(P[gRep[g]], gTris[g], B0, BN0).w, TARGET * Mathf.Clamp01(moveFrac[g] / 0.25f));
var GD = new Vector3[ng];
// 布の頂点ごとに、SHAPE=100 の肌との距離が c0 より近ければ、肌の法線の向きに押し出す
System.Func<int> enforce = () => {
  int n = 0;
  foreach (var g in actG) { var r = sdist(P[gRep[g]] + GD[g], gTris[g], B1, BN1); float need = c0[g] - r.w; if (need > 0) { GD[g] += new Vector3(r.x, r.y, r.z) * need; n++; } }
  return n;
};
// 乳首の側から見て、布の面から TARGET 以上離れていない所を押し出す。布の頂点ごとの押し出しだけでは、頂点の間の面から乳首の先が抜けることがある
var baseTip = new System.Collections.Generic.Dictionary<int, float>();
foreach (var bi in big) { var r = tipDist(B0[bi], bi, zero, nTmp); baseTip[bi] = r.z < 0 || r.y > COVER ? 1e9f : r.x; }
System.Func<int> tipPush = () => {
  int n = 0; var push = new Vector3[ng]; var pc = new int[ng];
  foreach (var bi in big) {
    var r = tipDist(B1[bi], bi, GD, nTmp);
    if (r.z < 0 || r.y > COVER) continue;
    float want = Mathf.Min(TARGET, baseTip[bi]);  // 元の設計で食い込んでいる所は、元の食い込みまでで止める
    float need = want - r.x; if (need <= 0) continue;
    int t = (int)r.z; foreach (var v in new[] { RT[t], RT[t + 1], RT[t + 2] }) { push[grp[v]] += nTmp[0] * need; pc[grp[v]]++; }
    n++;
  }
  for (int g = 0; g < ng; g++) if (pc[g] > 0) GD[g] += push[g] / pc[g] * 1.2f;
  return n;
};
// 乳首ごとの先（SHAPE=100 の肌で一番動いた頂点）と向き
var tipT = new System.Collections.Generic.List<Vector3>(); var tipN = new System.Collections.Generic.List<Vector3>(); var tipName = new System.Collections.Generic.List<string>();
foreach (var side in new[] { 1f, -1f }) {
  int tipI = -1; float tipD = 0;
  foreach (var i in big) { if (Mathf.Sign(avatarRoot.InverseTransformPoint(B1[i]).x) != side) continue; float d = (B1[i] - B0[i]).magnitude; if (d > tipD) { tipD = d; tipI = i; } }
  if (tipI < 0) continue;
  tipT.Add(B1[tipI]); tipN.Add((B1[tipI] - B0[tipI]) / tipD); tipName.Add(side > 0 ? "+x" : "-x");
}

// 1) つまむ: 乳首の先の布をつまんで引き出したような形にする。布の各頂点を、その頂点の法線の向きに D * (1 - q^2)^2（q = 軸からの距離 / PINCH_R）出す。
// 素体のキーは乳首の周り 2cm ほどの肌も持ち上げるので、布も広く滑らかに出す。
// 向きを乳首の向き 1 つにすると、乳首が外を向いている分だけ胸の側面で布が途切れ、上から見て段になる
var pinchD = new float[tipT.Count]; var pinchLog = new System.Collections.Generic.List<string>();
var actSet = new System.Collections.Generic.HashSet<int>(actG);
for (int si = 0; si < tipT.Count; si++) {
  var T = tipT[si]; var Dn = tipN[si];
  var gs = new System.Collections.Generic.List<int>(); var fs = new System.Collections.Generic.List<float>(); float D = 0;
  // 肌から遠い布（NEAR の外）も含める。含めないと、つまんだ形が NEAR の所で切れて段になる
  for (int g = 0; g < ng; g++) {
    var X = P[gRep[g]] - T; float hx = Vector3.Dot(X, Dn); if (Mathf.Abs(hx) > PINCH_R) continue;
    float q = (X - Dn * hx).magnitude / PINCH_R; if (q >= 1) continue;
    float f = (1 - q * q) * (1 - q * q); gs.Add(g); fs.Add(f);
    if (q >= PINCH_CORE || !actSet.Contains(g)) continue;
    var dir = Nn[gRep[g]]; var r = sdist(P[gRep[g]], gTris[g], B1, BN1);
    D = Mathf.Max(D, Mathf.Max(0, c0[g] - r.w) / Mathf.Max(Vector3.Dot(new Vector3(r.x, r.y, r.z), dir), 0.3f) / f);
  }
  for (int k = 0; k < gs.Count; k++) { var dir = Nn[gRep[gs[k]]]; float up = D * fs[k] - Vector3.Dot(GD[gs[k]], dir); if (up > 0) GD[gs[k]] += dir * up; }
  pinchD[si] = D; pinchLog.Add(tipName[si] + " " + (D * 1000).ToString("F2") + "mm (" + gs.Count + " verts)");
}
sb.AppendLine("pinch: " + string.Join(", ", pinchLog));
// 2) 足りない所を押し出す。押し出しは点ごとに出るので、押し出した分だけをならしてから押し直す（つまんだ形の上に凹凸を作らない）
var pinched = (Vector3[])GD.Clone(); var pushLog = new System.Collections.Generic.List<int>();
for (int pass = 0; pass < 8; pass++) {
  pushLog.Add(enforce());
  var E = new Vector3[ng]; for (int g = 0; g < ng; g++) E[g] = GD[g] - pinched[g];
  for (int it = 0; it < PUSH_SMOOTH; it++) { var nE = (Vector3[])E.Clone(); for (int g = 0; g < ng; g++) { if (!active[g] || adj[g].Count == 0) continue; Vector3 sm = Vector3.zero; foreach (var h in adj[g]) sm += E[h]; nE[g] = E[g] * 0.5f + sm / adj[g].Count * 0.5f; } E = nE; }
  for (int g = 0; g < ng; g++) GD[g] = pinched[g] + E[g];
}
for (int pass = 0; pass < 4; pass++) pushLog.Add(enforce());
for (int pass = 0; pass < 6; pass++) { int n = tipPush(); pushLog.Add(n); if (n == 0) break; enforce(); }
float extra = 0; foreach (var g in actG) extra = Mathf.Max(extra, (GD[g] - pinched[g]).magnitude);
sb.AppendLine("pushed per pass: " + string.Join(",", pushLog) + " (max " + (extra * 1000).ToString("F2") + "mm beyond the pinch)");
// 3) 凸包まで持ち上げる: 乳首の周りの布を、その範囲の頂点の凸包の面まで法線の向きに出す。凸な形はどの向きから見ても輪郭が凸になり、
// 上や横から見て山の上に凹みや段が出ない。先の上面（HULL_TOP / HULL_APEX）を加えると、先が平らな乳首に張ったテントの形になり、
// 上から見て先に 3 つほどの角が並ぶ。1 頂点だけを出すと、先が 1 点だけ尖る。凸包は、たくさんの向きの支持平面で外から近似する
if (HULL_R > 0) {
  var hullLog = new System.Collections.Generic.List<string>();
  var dirs = new System.Collections.Generic.List<Vector3>();
  for (int i = 0; i < 6000; i++) { float y = 1 - 2 * (i + 0.5f) / 6000f, rr = Mathf.Sqrt(1 - y * y), th = i * 2.39996323f; dirs.Add(new Vector3(Mathf.Cos(th) * rr, y, Mathf.Sin(th) * rr)); }
  for (int si = 0; si < tipT.Count; si++) {
    var T = tipT[si]; var Dn = tipN[si];
    // 布が乳首を覆っていない（つまむ必要がなかった）側は触らない。凸包まで出すと、レースや浮いたカップの形が変わる
    if (pinchD[si] < 0.001f) { hullLog.Add(tipName[si] + " skipped (no pinch)"); continue; }
    // 範囲内の、外を向いた布の頂点（裏地など内向きの面は凸包に入れず、持ち上げもしない）
    var hs = new System.Collections.Generic.List<int>(); var hq = new System.Collections.Generic.List<float>(); var hp = new System.Collections.Generic.List<Vector3>();
    for (int g = 0; g < ng; g++) {
      var X = P[gRep[g]] - T; float hx = Vector3.Dot(X, Dn); if (Mathf.Abs(hx) > HULL_R) continue;
      float l = (X - Dn * hx).magnitude; if (l >= HULL_R) continue;
      if (Vector3.Dot(Nn[gRep[g]], Dn) < 0.2f) continue;
      hs.Add(g); hq.Add(l / HULL_R); hp.Add(P[gRep[g]] + GD[g]);
    }
    if (hs.Count < 4) continue;
    var pts = new System.Collections.Generic.List<Vector3>(hp);
    for (int j = 0; j < hs.Count; j++) if (hq[j] * HULL_R < HULL_TOP) pts.Add(hp[j] + Nn[gRep[hs[j]]] * HULL_APEX);
    var sup = new float[dirs.Count];
    for (int k = 0; k < dirs.Count; k++) { float m = -1e9f; foreach (var x in pts) m = Mathf.Max(m, Vector3.Dot(dirs[k], x)); sup[k] = m; }
    int lifted = 0; float maxUp = 0;
    for (int j = 0; j < hs.Count; j++) {
      var n = Nn[gRep[hs[j]]]; float t = 1e9f;
      for (int k = 0; k < dirs.Count; k++) { float un = Vector3.Dot(dirs[k], n); if (un < 0.3f) continue; t = Mathf.Min(t, (sup[k] - Vector3.Dot(dirs[k], hp[j])) / un); }
      if (t <= 0 || t > 0.01f) continue;
      // 範囲の外側 4 割で 0 まで弱め、縁に段を作らない
      float w = Mathf.Clamp01((1 - hq[j]) / 0.4f); w = w * w * (3 - 2 * w);
      GD[hs[j]] += n * (t * w); lifted++; maxUp = Mathf.Max(maxUp, t * w);
    }
    hullLog.Add(tipName[si] + " lifted " + lifted + "/" + hs.Count + " verts, max " + (maxUp * 1000).ToString("F2") + "mm");
  }
  sb.AppendLine("hull: " + string.Join(", ", hullLog));
}
sb.AppendLine("after:  " + SHAPE + "=100 " + skinCheck(B1, GD));

// ワールドの差分 → メッシュ空間（頂点ごとのスキン行列の逆）
var bones = cloth.bones; var bindposes = srcMesh.bindposes; var boneWeights = srcMesh.boneWeights;
var outD = new Vector3[nv]; float maxW = 0; int moved = 0;
for (int i = 0; i < nv; i++) {
  var d = GD[grp[i]]; if (d.sqrMagnitude < 1e-12f) continue;
  var w = boneWeights[i]; Matrix4x4 M = new Matrix4x4();
  System.Action<int, float> acc = (bi, ww) => { if (ww <= 0) return; var m = bones[bi].localToWorldMatrix * bindposes[bi]; for (int r = 0; r < 16; r++) M[r] += m[r] * ww; };
  acc(w.boneIndex0, w.weight0); acc(w.boneIndex1, w.weight1); acc(w.boneIndex2, w.weight2); acc(w.boneIndex3, w.weight3);
  outD[i] = M.inverse.MultiplyVector(d); maxW = Mathf.Max(maxW, d.magnitude); moved++;
}
sb.AppendLine("cloth moved verts=" + moved + " max mm=" + (maxW * 1000).ToString("F2"));
if (!APPLY) return sb.ToString();

if (SYNC_ONLY) {
  if (existing < 0) return sb + "SYNC_ONLY needs an existing " + SHAPE + " on the cloth";
  UnityEditor.Undo.RecordObject(cloth, "Use " + SHAPE);
  cloth.SetBlendShapeWeight(existing, orig);
  UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(cloth);
  sb.AppendLine("kept the existing " + SHAPE + " (mesh unchanged), weight=" + orig);
} else {
  // 書き出し: 複製したメッシュに SHAPE を足す（OVERWRITE の時は他のキーを積み直す）
  var nm = UnityEngine.Object.Instantiate(srcMesh); nm.name = origMesh.name;
  if (existing >= 0) {
    nm.ClearBlendShapes();
    var dv = new Vector3[nv]; var dn = new Vector3[nv]; var dt = new Vector3[nv];
    for (int s = 0; s < srcMesh.blendShapeCount; s++) {
      if (s == existing) continue;
      for (int f = 0; f < srcMesh.GetBlendShapeFrameCount(s); f++) {
        srcMesh.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
        nm.AddBlendShapeFrame(srcMesh.GetBlendShapeName(s), srcMesh.GetBlendShapeFrameWeight(s, f), dv, dn, dt);
      }
    }
  }
  nm.AddBlendShapeFrame(SHAPE, 100, outD, null, null);
  string outPath = OUT;
  var srcPath = UnityEditor.AssetDatabase.GetAssetPath(origMesh);
  if (outPath == "" && existing >= 0 && srcPath.EndsWith(".asset")) {
    // 作り直しで、今のメッシュが単独の .asset（前回の書き出し）なら、そこへ上書きする。
    // CreateAsset はメッシュの名前をファイル名に変えるので、名前から組み立てると別のファイルになる
    outPath = srcPath;
  } else if (outPath == "") {
    var prefabPath = UnityEditor.PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(clothGo);
    var dir = prefabPath != "" ? System.IO.Path.GetDirectoryName(prefabPath) : System.IO.Path.GetDirectoryName(UnityEditor.AssetDatabase.GetAssetPath(origMesh));
    outPath = (dir + "/" + origMesh.name + "_" + SHAPE + ".asset").Replace('\\', '/');
  }
  var names = new string[srcMesh.blendShapeCount]; var weights = new float[srcMesh.blendShapeCount];
  for (int i = 0; i < names.Length; i++) { names[i] = srcMesh.GetBlendShapeName(i); weights[i] = cloth.GetBlendShapeWeight(i); }
  if (srcPath == outPath) {
    // 前回この手順で書き出したメッシュを作り直す時は、同じアセットへ中身を写す（GUID を保つ）。
    // 空にしてから写す。CopySerialized だけだと GPU 側の頂点バッファが作り直されず、頂点数が変わった時（SUBDIVIDE）に
    // 古い大きさのまま残って、そのメッシュを使うレンダラーの描画が止まる（Rendering stopped ... vertex stride）
    origMesh.Clear(false);
    UnityEditor.EditorUtility.CopySerialized(nm, origMesh); UnityEngine.Object.DestroyImmediate(nm);
    UnityEditor.EditorUtility.SetDirty(origMesh); UnityEditor.AssetDatabase.SaveAssetIfDirty(origMesh);
  } else {
    if (UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(outPath) != null) return sb + "OUT already exists: " + outPath;
    UnityEditor.AssetDatabase.CreateAsset(nm, outPath);
  }
  // 書き出したアセットを読み込み直したものをレンダラーに入れる
  UnityEditor.AssetDatabase.ImportAsset(outPath, UnityEditor.ImportAssetOptions.ForceSynchronousImport | UnityEditor.ImportAssetOptions.ForceUpdate);
  nm = UnityEditor.AssetDatabase.LoadAssetAtPath<Mesh>(outPath);
  UnityEditor.Undo.RecordObject(cloth, "Create " + SHAPE);
  cloth.sharedMesh = nm;
  for (int i = 0; i < names.Length; i++) { int j = nm.GetBlendShapeIndex(names[i]); if (j >= 0) cloth.SetBlendShapeWeight(j, weights[i]); }
  cloth.SetBlendShapeWeight(nm.GetBlendShapeIndex(SHAPE), orig);
  UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(cloth);
  sb.AppendLine("saved " + outPath + " (" + SHAPE + "=" + orig + ")");

}
if (ADD_SYNC) {
  // Modular Avatar はリフレクションで使う（入っていないプロジェクトでもコンパイルできるように）
  System.Type syncT = null, bindT = null, refT = null;
  foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies()) {
    if (syncT == null) syncT = asm.GetType("nadena.dev.modular_avatar.core.ModularAvatarBlendshapeSync");
    if (bindT == null) bindT = asm.GetType("nadena.dev.modular_avatar.core.BlendshapeBinding");
    if (refT == null) refT = asm.GetType("nadena.dev.modular_avatar.core.AvatarObjectReference");
  }
  if (syncT == null || bindT == null || refT == null) {
    sb.AppendLine("Modular Avatar not found: sync skipped (drive cloth " + SHAPE + " together with the body yourself)");
  } else {
    var sync = clothGo.GetComponent(syncT);
    if (sync == null) sync = UnityEditor.Undo.AddComponent(clothGo, syncT);
    UnityEditor.Undo.RecordObject(sync, "Sync " + SHAPE);
    var list = (System.Collections.IList)syncT.GetField("Bindings").GetValue(sync);
    var fRef = bindT.GetField("ReferenceMesh"); var fSrc = bindT.GetField("Blendshape"); var fLocal = bindT.GetField("LocalBlendshape");
    // CreateInstance(refT, body.gameObject) と書くと、UnityEngine.Object の bool への暗黙変換で
    // CreateInstance(Type, bool) が選ばれ、参照先が空になる。必ず object[] で渡す
    System.Func<object> newRef = () => System.Activator.CreateInstance(refT, new object[] { body.gameObject });
    int idx = -1; for (int i = 0; i < list.Count; i++) if ((string)fLocal.GetValue(list[i]) == SHAPE) idx = i;
    object nb = idx >= 0 ? list[idx] : System.Activator.CreateInstance(bindT); // 構造体なので箱に入れたまま値を入れる
    fRef.SetValue(nb, newRef()); fSrc.SetValue(nb, SHAPE); fLocal.SetValue(nb, SHAPE);
    if (idx >= 0) list[idx] = nb; else list.Add(nb);
    UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(sync);
    var fPath = refT.GetField("referencePath");
    foreach (var b in list) sb.AppendLine("sync " + fLocal.GetValue(b) + " <- " + fPath.GetValue(fRef.GetValue(b)) + "/" + fSrc.GetValue(b));
  }
}
return sb.ToString();
} finally {
  // 試運転や途中で止まった時は、細かく割ったメッシュを外して元に戻す
  if (subMesh != null) { if (cloth.sharedMesh == subMesh) useMesh(origMesh); UnityEngine.Object.DestroyImmediate(subMesh); }
}
