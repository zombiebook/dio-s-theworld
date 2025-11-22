using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace timestop
{
    // Duckov 모드 로더 엔트리
    public class ModBehaviour : global::Duckov.Modding.ModBehaviour
    {
        protected override void OnAfterSetup()
        {
            try
            {
                GameObject root = new GameObject("TimeStopRoot");
                UnityEngine.Object.DontDestroyOnLoad(root);
                root.AddComponent<TimeStopManager>();

                Debug.Log("[TimeStop] OnAfterSetup - TimeStopManager 생성 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] OnAfterSetup 예외: " + ex);
            }
        }
    }

    public class TimeStopManager : MonoBehaviour
    {
        // ───── 설정값 ─────
        private KeyCode _mouseKey = KeyCode.Mouse2; // 마우스 가운데
        private KeyCode _altKey   = KeyCode.F7;     // 예비 키
        private float _stopDuration = 9.0f;         // 정지 유지 시간 (Realtime)
        private float _cooldown    = 15.0f;         // 쿨타임 (Realtime)

        // ───── 상태값 ─────
        private bool _isActive;
        private float _lastUseTime = -999f;
        private bool _initialized;

        // 플레이어 (있으면 기록만, 없어도 동작)
        private MonoBehaviour _playerCtrl;
        private Transform _playerRoot;

        // 정지 대상 정보들
        private sealed class FrozenChar
        {
            public Transform root;
            public Vector3 pos;
            public Quaternion rot;
        }

        private sealed class FrozenRigidbody
        {
            public Rigidbody rb;
            public bool wasKinematic;
            public Vector3 velocity;
            public Vector3 angularVelocity;
        }

        private sealed class FrozenAnimator
        {
            public Animator animator;
            public float speed;
        }

        private sealed class FrozenBehaviour
        {
            public MonoBehaviour behaviour;
            public bool wasEnabled;
        }

        private readonly List<FrozenChar> _frozenChars = new List<FrozenChar>();
        private readonly List<FrozenRigidbody> _frozenRigidbodies = new List<FrozenRigidbody>();
        private readonly List<FrozenAnimator> _frozenAnimators = new List<FrozenAnimator>();
        private readonly List<FrozenBehaviour> _frozenBehaviours = new List<FrozenBehaviour>();

        // ───────────────── 화면 플래시 (3D Quad + GUI) ─────────────────
        private GameObject _flashObj;
        private Material _flashMat;
        private float _flashMaxAlpha = 0.8f;

        private Texture2D _flashTex;
        private float _flashGuiAlpha;

        // ───────────────── 조조 느낌 레이(빛의 실) 이펙트 ─────────────────
        private readonly List<LineRenderer> _effectRays = new List<LineRenderer>();
        private Material _rayMaterial;
        private float _rayAlpha;
        private Color _rayBaseColor = new Color(0.3f, 1.0f, 0.6f, 0.9f); // 에메랄드빛

        private void Awake()
        {
            Debug.Log("[TimeStop] TimeStopManager.Awake");
            SetupFlashQuad();
            SetupFlashTexture();
            _rayAlpha = 0f;
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) return;

            // 마우스 가운데 또는 F7 둘 중 하나로 발동
            bool keyDown = UnityEngine.Input.GetKeyDown(_mouseKey)
                           || UnityEngine.Input.GetKeyDown(_altKey);

            if (keyDown)
            {
                Debug.Log("[TimeStop] 발동 키 입력 감지");
            }

            if (keyDown)
            {
                if (!_isActive && Time.unscaledTime >= _lastUseTime + _cooldown)
                {
                    Debug.Log("[TimeStop] 시간정지 코루틴 시작");
                    StartCoroutine(TimeStopRoutine());
                }
                else
                {
                    Debug.Log("[TimeStop] 쿨타임 중이거나 이미 활성화됨");
                }
            }

            // 활성화 중일 때 3D 플래시 페이드 아웃
            if (_isActive && _flashMat != null)
            {
                Color c = _flashMat.color;
                if (c.a > 0f)
                {
                    c.a = Mathf.MoveTowards(c.a, 0f, Time.unscaledDeltaTime * 2f);
                    _flashMat.color = c;
                }
            }

            // GUI 플래시도 페이드 아웃
            if (_flashGuiAlpha > 0f)
            {
                _flashGuiAlpha = Mathf.MoveTowards(_flashGuiAlpha, 0f, Time.unscaledDeltaTime * 2f);
            }

            // 레이(빛의 실) 알파 업데이트
            if (_effectRays.Count > 0)
            {
                if (_isActive)
                {
                    // 정지 중에는 거의 풀 알파 유지
                    _rayAlpha = Mathf.MoveTowards(_rayAlpha, 1f, Time.unscaledDeltaTime * 4f);
                }
                else
                {
                    // 정지 해제 후 서서히 사라짐
                    _rayAlpha = Mathf.MoveTowards(_rayAlpha, 0f, Time.unscaledDeltaTime * 2f);
                    if (_rayAlpha <= 0.01f)
                    {
                        ClearRays();
                    }
                }

                UpdateRayColors();
            }
        }

        private void LateUpdate()
        {
            if (!_isActive) return;

            int count = _frozenChars.Count;
            for (int i = 0; i < count; i++)
            {
                FrozenChar fc = _frozenChars[i];
                if (fc.root != null)
                {
                    fc.root.position = fc.pos;
                    fc.root.rotation = fc.rot;
                }
            }
        }

        private IEnumerator TimeStopRoutine()
        {
            _isActive = true;
            _lastUseTime = Time.unscaledTime;

            Debug.Log("[TimeStop] TimeStopRoutine 시작");

            FindPlayerCharacter();
            PlayFlash(); // 번쩍 + 레이 생성

            // 연출용 짧은 딜레이
            yield return new WaitForSecondsRealtime(0.15f);

            FreezeWorldExceptPlayer();

            Debug.Log("[TimeStop] 세계 정지 ON (플레이어만 이동 가능)");

            float start = Time.unscaledTime;
            while (Time.unscaledTime - start < _stopDuration)
            {
                yield return null;
            }

            UnfreezeWorld();
            HideFlash();

            Debug.Log("[TimeStop] 세계 정지 OFF");
            _isActive = false;
        }

        // ───────────────── 플레이어 판별 헬퍼 ─────────────────

        private bool IsPlayerCharacter(MonoBehaviour mb)
        {
            if (mb == null) return false;

            try
            {
                Type t = mb.GetType();
                if (!t.Name.Contains("CharacterMainControl"))
                    return false;

                FieldInfo fTeam = t.GetField(
                    "team",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (fTeam != null)
                {
                    object v = fTeam.GetValue(mb);
                    if (v != null)
                    {
                        string s = v.ToString().ToLowerInvariant();
                        if (s.Contains("player"))
                            return true;
                    }
                }
            }
            catch
            {
                // 무시
            }

            return false;
        }

        private bool IsUnderPlayerCharacter(Transform tr)
        {
            if (tr == null) return false;

            Transform cur = tr;
            while (cur != null)
            {
                MonoBehaviour[] mbs = cur.GetComponents<MonoBehaviour>();
                for (int i = 0; i < mbs.Length; i++)
                {
                    if (IsPlayerCharacter(mbs[i]))
                        return true;
                }

                cur = cur.parent;
            }

            return false;
        }

        private void FindPlayerCharacter()
        {
            _playerCtrl = null;
            _playerRoot = null;

            try
            {
                MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                Camera cam = Camera.main;
                if (cam == null)
                {
                    Debug.Log("[TimeStop] Camera.main 없음 - 플레이어 추정 생략");
                    return;
                }

                float bestDist = 999999f;

                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i];
                    if (mb == null) continue;

                    Type t = mb.GetType();
                    if (!t.Name.Contains("CharacterMainControl"))
                        continue;

                    Transform tr = mb.transform;
                    float dist = Vector3.Distance(tr.position, cam.transform.position);

                    if (_playerCtrl == null || dist < bestDist)
                    {
                        _playerCtrl = mb;
                        _playerRoot = tr;
                        bestDist = dist;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] FindPlayerCharacter 예외: " + ex);
            }

            if (_playerCtrl != null)
            {
                Debug.Log("[TimeStop] 플레이어 후보 CharacterMainControl: " + _playerCtrl.name);
            }
            else
            {
                Debug.Log("[TimeStop] 플레이어 후보를 찾지 못함 (team 기반 필터만 사용)");
            }
        }

        // ───────────────── 세계 정지 / 해제 ─────────────────

        private void FreezeWorldExceptPlayer()
        {
            _frozenChars.Clear();
            _frozenRigidbodies.Clear();
            _frozenAnimators.Clear();
            _frozenBehaviours.Clear();

            try
            {
                // 1) CharacterMainControl 들 (플레이어 team 제외) 정지
                MonoBehaviour[] all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                for (int i = 0; i < all.Length; i++)
                {
                    MonoBehaviour mb = all[i];
                    if (mb == null) continue;

                    Type t = mb.GetType();
                    if (!t.Name.Contains("CharacterMainControl"))
                        continue;

                    // team 이 player면 무조건 건드리지 않음 (플레이어)
                    if (IsPlayerCharacter(mb))
                        continue;

                    Transform root = mb.transform;

                    // (보조) 플레이어 후보 루트와 같거나 자식이면 스킵
                    if (_playerRoot != null &&
                        (root == _playerRoot || root.IsChildOf(_playerRoot)))
                        continue;

                    // ── 위치/회전 저장 (몸 얼리기) ──
                    FrozenChar fc = new FrozenChar();
                    fc.root = root;
                    fc.pos = root.position;
                    fc.rot = root.rotation;
                    _frozenChars.Add(fc);

                    // ── Animator 정지 ──
                    Animator[] anims = root.GetComponentsInChildren<Animator>(true);
                    for (int j = 0; j < anims.Length; j++)
                    {
                        Animator anim = anims[j];
                        if (anim == null) continue;

                        FrozenAnimator fa = new FrozenAnimator();
                        fa.animator = anim;
                        fa.speed = anim.speed;
                        _frozenAnimators.Add(fa);

                        anim.speed = 0f;
                    }

                    // ── Rigidbody 정지 (원래 키네마틱은 건드리지 않음) ──
                    Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
                    for (int j = 0; j < bodies.Length; j++)
                    {
                        Rigidbody rb = bodies[j];
                        if (rb == null) continue;

                        if (rb.isKinematic) continue;

                        FrozenRigidbody fr = new FrozenRigidbody();
                        fr.rb = rb;
                        fr.wasKinematic = rb.isKinematic; // false
                        fr.velocity = rb.velocity;
                        fr.angularVelocity = rb.angularVelocity;
                        _frozenRigidbodies.Add(fr);

                        rb.isKinematic = true;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }

                    // ── 적 쪽 모든 스크립트 끄기 (MonoBehaviour.enabled = false) ──
                    MonoBehaviour[] scripts = root.GetComponentsInChildren<MonoBehaviour>(true);
                    for (int j = 0; j < scripts.Length; j++)
                    {
                        MonoBehaviour comp = scripts[j];
                        if (comp == null) continue;

                        // 혹시 모를 플레이어 스크립트 보호
                        if (IsPlayerCharacter(comp))
                            continue;

                        if (!comp.enabled) continue; // 원래 꺼져있던 건 건들지 않음

                        FrozenBehaviour fb = new FrozenBehaviour();
                        fb.behaviour = comp;
                        fb.wasEnabled = true;
                        _frozenBehaviours.Add(fb);

                        comp.enabled = false;
                    }
                }

                // 2) 나머지 Rigidbody (탄환 등)도, 플레이어 캐릭터 밑에 있는 건 건드리지 않음
                Rigidbody[] allRb = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                for (int i = 0; i < allRb.Length; i++)
                {
                    Rigidbody rb = allRb[i];
                    if (rb == null) continue;

                    if (rb.isKinematic) continue; // 원래 키네마틱은 냅둔다

                    Transform tr = rb.transform;

                    // 플레이어 캐릭터 밑이면 스킵
                    if (IsUnderPlayerCharacter(tr))
                        continue;

                    // 이미 처리한 Rigidbody면 스킵
                    bool already = false;
                    for (int j = 0; j < _frozenRigidbodies.Count; j++)
                    {
                        if (_frozenRigidbodies[j].rb == rb)
                        {
                            already = true;
                            break;
                        }
                    }
                    if (already) continue;

                    FrozenRigidbody fr2 = new FrozenRigidbody();
                    fr2.rb = rb;
                    fr2.wasKinematic = rb.isKinematic; // false
                    fr2.velocity = rb.velocity;
                    fr2.angularVelocity = rb.angularVelocity;
                    _frozenRigidbodies.Add(fr2);

                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                Debug.Log("[TimeStop] 정지된 캐릭터=" + _frozenChars.Count +
                          ", 리지드바디=" + _frozenRigidbodies.Count +
                          ", 애니메이터=" + _frozenAnimators.Count +
                          ", 비활성화 스크립트=" + _frozenBehaviours.Count);
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] FreezeWorldExceptPlayer 예외: " + ex);
            }
        }

        private void UnfreezeWorld()
        {
            try
            {
                for (int i = 0; i < _frozenChars.Count; i++)
                {
                    FrozenChar fc = _frozenChars[i];
                    if (fc.root != null)
                    {
                        fc.root.position = fc.pos;
                        fc.root.rotation = fc.rot;
                    }
                }
                _frozenChars.Clear();

                for (int i = 0; i < _frozenAnimators.Count; i++)
                {
                    FrozenAnimator fa = _frozenAnimators[i];
                    if (fa.animator != null)
                    {
                        fa.animator.speed = fa.speed;
                    }
                }
                _frozenAnimators.Clear();

                for (int i = 0; i < _frozenRigidbodies.Count; i++)
                {
                    FrozenRigidbody fr = _frozenRigidbodies[i];
                    if (fr.rb != null)
                    {
                        fr.rb.isKinematic = fr.wasKinematic; // 원래 false
                        fr.rb.velocity = fr.velocity;
                        fr.rb.angularVelocity = fr.angularVelocity;
                    }
                }
                _frozenRigidbodies.Clear();

                // 꺼놨던 스크립트 다시 켜기
                for (int i = 0; i < _frozenBehaviours.Count; i++)
                {
                    FrozenBehaviour fb = _frozenBehaviours[i];
                    if (fb.behaviour != null)
                    {
                        fb.behaviour.enabled = fb.wasEnabled;
                    }
                }
                _frozenBehaviours.Clear();
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] UnfreezeWorld 예외: " + ex);
            }
        }

        // ───────────────── 화면 플래시 (3D Quad) ─────────────────

        private void SetupFlashQuad()
        {
            if (_flashObj != null) return;

            try
            {
                _flashObj = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _flashObj.name = "TimeStopFlash";
                UnityEngine.Object.DontDestroyOnLoad(_flashObj);

                Collider col = _flashObj.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);

                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Standard");

                if (shader == null)
                {
                    Debug.Log("[TimeStop] 경고: 사용할 Shader를 찾지 못해 Quad 플래시는 비활성화됨");
                    _flashObj.SetActive(false);
                    _flashObj = null;
                    _flashMat = null;
                    return;
                }

                _flashMat = new Material(shader);
                if (_flashMat.HasProperty("_Color"))
                    _flashMat.SetColor("_Color", new Color(1.0f, 0.95f, 0.2f, 0.0f));
                else if (_flashMat.HasProperty("_BaseColor"))
                    _flashMat.SetColor("_BaseColor", new Color(1.0f, 0.95f, 0.2f, 0.0f));

                MeshRenderer r = _flashObj.GetComponent<MeshRenderer>();
                r.material = _flashMat;

                _flashMat.renderQueue = 4000;

                AttachToMainCamera();
                HideFlash();

                Debug.Log("[TimeStop] 플래시 Quad 셋업 완료");
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] SetupFlashQuad 예외: " + ex);
                _flashObj = null;
                _flashMat = null;
            }
        }

        private void AttachToMainCamera()
        {
            if (_flashObj == null) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            _flashObj.transform.SetParent(cam.transform, false);
            _flashObj.transform.localPosition = new Vector3(0f, 0f, 1f);
            _flashObj.transform.localRotation = Quaternion.identity;

            float h = Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) * 2f;
            float w = h * cam.aspect;
            _flashObj.transform.localScale = new Vector3(w, h, 1f);
        }

        // ───────────────── 화면 플래시 (GUI 오버레이) ─────────────────

        private void SetupFlashTexture()
        {
            if (_flashTex != null) return;

            try
            {
                _flashTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _flashTex.SetPixel(0, 0, new Color(1.0f, 0.95f, 0.2f, 1.0f));
                _flashTex.Apply();
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] SetupFlashTexture 예외: " + ex);
                _flashTex = null;
            }
        }

        private void PlayFlash()
        {
            // 3D Quad 플래시
            if (_flashObj == null || _flashMat == null)
            {
                SetupFlashQuad();
            }

            if (_flashObj != null && _flashMat != null)
            {
                AttachToMainCamera();

                _flashObj.SetActive(true);

                Color c = _flashMat.color;
                c.a = _flashMaxAlpha;
                _flashMat.color = c;
            }

            // GUI 플래시
            if (_flashTex == null)
            {
                SetupFlashTexture();
            }

            _flashGuiAlpha = _flashMaxAlpha;

            // 조조 느낌 레이 생성
            _rayAlpha = 1f;
            SpawnRays();
            UpdateRayColors();
        }

        private void HideFlash()
        {
            if (_flashObj != null)
                _flashObj.SetActive(false);

            _flashGuiAlpha = 0f;

            // 정지 해제 시 레이는 서서히 사라지게 두고,
            // _rayAlpha는 Update에서 0까지 내려가면 ClearRays 호출
        }

        private void OnLevelWasLoaded(int level)
        {
            AttachToMainCamera();
        }

        private void OnGUI()
        {
            // GUI 플래시
            if (_flashGuiAlpha <= 0f) return;
            if (_flashTex == null) return;

            Color old = UnityEngine.GUI.color;
            Color c = new Color(1.0f, 0.95f, 0.2f, _flashGuiAlpha);
            UnityEngine.GUI.color = c;

            UnityEngine.GUI.DrawTexture(
                new Rect(0f, 0f, Screen.width, Screen.height),
                _flashTex
            );

            UnityEngine.GUI.color = old;
        }

        // ───────────────── 조조 레이(빛의 실) 이펙트 ─────────────────

        private void SetupRayMaterial()
        {
            if (_rayMaterial != null) return;

            try
            {
                Shader shader = Shader.Find("Unlit/Color");
                if (shader == null) shader = Shader.Find("Sprites/Default");
                if (shader == null) shader = Shader.Find("Standard");

                if (shader == null)
                {
                    Debug.Log("[TimeStop] 레이용 Shader 찾기 실패 - 레이 이펙트 비활성");
                    return;
                }

                _rayMaterial = new Material(shader);
                if (_rayMaterial.HasProperty("_Color"))
                    _rayMaterial.SetColor("_Color", _rayBaseColor);
                else if (_rayMaterial.HasProperty("_BaseColor"))
                    _rayMaterial.SetColor("_BaseColor", _rayBaseColor);

                _rayMaterial.renderQueue = 4000;
            }
            catch (Exception ex)
            {
                Debug.Log("[TimeStop] SetupRayMaterial 예외: " + ex);
                _rayMaterial = null;
            }
        }

        private Vector3 GetEffectCenter()
        {
            if (_playerRoot != null)
                return _playerRoot.position + Vector3.up * 1.2f;

            Camera cam = Camera.main;
            if (cam != null)
                return cam.transform.position + cam.transform.forward * 5f;

            return Vector3.zero;
        }

        private void SpawnRays()
        {
            ClearRays();
            SetupRayMaterial();
            if (_rayMaterial == null) return;

            Vector3 center = GetEffectCenter();

            int rayCount = 40;              // 레이 개수 (원하면 여기 늘려도 됨)
            float innerRadius = 3.0f;       // 중심 근처 빈 공간
            float outerRadius = 12.0f;      // 레이가 시작되는 바깥 반경
            float minLength  = 6.0f;
            float maxLength  = 18.0f;

            for (int i = 0; i < rayCount; i++)
            {
                GameObject go = new GameObject("TimeStopRay");
                go.transform.position = center;
                UnityEngine.Object.DontDestroyOnLoad(go);

                LineRenderer lr = go.AddComponent<LineRenderer>();
                lr.material = _rayMaterial;
                lr.positionCount = 2;
                lr.useWorldSpace = true;
                lr.receiveShadows = false;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.widthMultiplier = 0.04f;

                // 화면 전체를 가로지르는 느낌의 랜덤 방향
                float yaw   = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float pitch = UnityEngine.Random.Range(-15f, 15f) * Mathf.Deg2Rad;

                float cosPitch = Mathf.Cos(pitch);
                Vector3 dir = new Vector3(
                    cosPitch * Mathf.Cos(yaw),
                    Mathf.Sin(pitch),
                    cosPitch * Mathf.Sin(yaw)
                ).normalized;

                float startR = UnityEngine.Random.Range(innerRadius, outerRadius);
                float length = UnityEngine.Random.Range(minLength, maxLength);

                // 멀리서 중심으로 날아오는 칼 느낌
                Vector3 end   = center + dir * startR;
                Vector3 start = center + dir * (startR + length);

                lr.SetPosition(0, start);
                lr.SetPosition(1, end);

                // 기본 색은 에메랄드빛, 알파는 _rayAlpha로 조절
                Color c = new Color(_rayBaseColor.r, _rayBaseColor.g, _rayBaseColor.b,
                                    _rayBaseColor.a * _rayAlpha);
                lr.startColor = c;
                lr.endColor   = c;

                _effectRays.Add(lr);
            }

            Debug.Log("[TimeStop] 레이 이펙트 생성 완료 - count=" + _effectRays.Count);
        }

        private void UpdateRayColors()
        {
            Color c = new Color(
                _rayBaseColor.r,
                _rayBaseColor.g,
                _rayBaseColor.b,
                _rayBaseColor.a * _rayAlpha
            );

            for (int i = 0; i < _effectRays.Count; i++)
            {
                LineRenderer lr = _effectRays[i];
                if (lr == null) continue;
                lr.startColor = c;
                lr.endColor   = c;
            }

            if (_rayMaterial != null)
            {
                if (_rayMaterial.HasProperty("_Color"))
                    _rayMaterial.color = c;
                else if (_rayMaterial.HasProperty("_BaseColor"))
                    _rayMaterial.SetColor("_BaseColor", c);
            }
        }

        private void ClearRays()
        {
            for (int i = 0; i < _effectRays.Count; i++)
            {
                LineRenderer lr = _effectRays[i];
                if (lr != null)
                {
                    GameObject go = lr.gameObject;
                    if (go != null)
                        UnityEngine.Object.Destroy(go);
                }
            }
            _effectRays.Clear();
        }
    }
}
