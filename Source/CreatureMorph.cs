using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace CreatureMorph
{
    [BepInPlugin(Id, "CreatureMorphBeta", "2.1.2")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "fr.p3lse.subnautica.creaturemorph";
        internal static Plugin Instance;
        internal bool MenuOpen;
        internal bool Morphed { get { return visual != null && owner != null; } }
        private Harmony harmony;
        private ConfigEntry<KeyCode> menuKey, returnKey, attackKey;
        private ConfigEntry<KeyCode> attackCycleKey;
        private ConfigEntry<float> sizeMultiplier;
        private ConfigEntry<float> soundVolume;
        private ConfigEntry<bool> unlimitedOxygen;
        private readonly Dictionary<Renderer, bool> hidden = new Dictionary<Renderer, bool>();
        private GameObject visual;
        private GameObject sourcePrefab;
        private CreatureSoundBank creatureSounds;
        private AudioSource unitySoundSource;
        private float nextAmbientSound;
        private Player owner;
        private Form form;
        private AttackMode attackMode = AttackMode.Primary;
        private Animator[] animators = new Animator[0];
        private readonly Dictionary<Animator, int> attackStates = new Dictionary<Animator, int>();
        private Transform shiftedCamera;
        private Vector3 cameraLocal;
        private Quaternion cameraRotation;
        private Camera renderingCamera;
        private float savedNearClip;
        private ConfigEntry<KeyCode> viewKey;
        private bool firstPerson;
        private float zoomDistance;
        private Vector3 modelCenter, eyeLocal;
        private Renderer[] creatureRenderers = new Renderer[0];
        private bool[] renderHidden = new bool[0];
        private float length, readyAt, attackUntil, hitAt;
        private bool hitPending, loading;
        private int generation;
        private Vector3 lastPosition;
        private string status = "Choose a creature, then press Transform.";
        private Vector2 scroll;
        private Rect window = new Rect(40, 60, 520, 650);
        private CursorLockMode previousLock;
        private bool previousCursor;
        private bool cursorPushed;
        private LiveMixinData originalHealthData, morphHealthData;
        private LiveMixin healthTarget;
        private float originalHealth, originalTempDamage, morphStarted, actualDamage;
        private Vector3 fullScale;
        private string search = "";
        private Form selected;
        private readonly List<AnimationRig> rigs = new List<AnimationRig>();
        private Renderer[] playerRenderers = new Renderer[0];
        private readonly RaycastHit[] castBuffer = new RaycastHit[64];
        private bool grounded;
        private float nextGroundProbe, nextRendererRefresh, groundLift, modelBottom;
        private Vector3 groundNormal = Vector3.up;
        private readonly Dictionary<TechType, string> unavailable = new Dictionary<TechType, string>();
        private GUIStyle menuTitle, menuSubtitle, menuButton, menuSelected, menuAction, menuSmall, menuBox;

        private enum AttackMode { Primary, Fireball, Teleport, EmpPulse }

        private sealed class Form
        {
            public readonly string Name;
            public readonly TechType Tech;
            public readonly float Damage, Length, Cooldown;
            public Form(string name, TechType tech, float damage, float size, float cooldown)
            { Name = name; Tech = tech; Damage = damage; Length = size; Cooldown = cooldown; }
        }
        private static readonly List<Form> Forms = new List<Form> {
            new Form("Stalker", TechType.Stalker, 25, 5, 1),
            new Form("Sandshark", TechType.Sandshark, 30, 4, 1.2f),
            new Form("Boneshark", TechType.BoneShark, 35, 5, 1.2f),
            new Form("Crabsnake", TechType.Crabsnake, 35, 6, 1.4f),
            new Form("Crabsquid", TechType.CrabSquid, 40, 6, 1.5f),
            new Form("Lava Lizard", TechType.LavaLizard, 35, 4, 1.2f),
            new Form("Reefback", TechType.Reefback, 30, 20, 2),
            new Form("Sea Treader", TechType.SeaTreader, 60, 18, 2),
            new Form("Reaper Leviathan", TechType.ReaperLeviathan, 80, 52, 2),
            new Form("Ghost Leviathan", TechType.GhostLeviathan, 85, 62, 2.2f),
            new Form("Ghost Leviathan Juvenile", TechType.GhostLeviathanJuvenile, 65, 28, 1.8f),
            new Form("Sea Dragon", TechType.SeaDragon, 100, 100, 2.5f),
            new Form("Warper", TechType.Warper, 35, 6, 1.3f),
            new Form("Shocker", TechType.Shocker, 40, 5, 1.5f),
            new Form("Spine Eel", TechType.SpineEel, 35, 8, 1.4f),
            new Form("Sea Emperor Baby", TechType.SeaEmperorBaby, 40, 10, 1.5f),
            new Form("Sea Emperor Juvenile", TechType.SeaEmperorJuvenile, 70, 30, 2)
        };

        private static void CompleteCatalogue()
        {
            // The catalogue is intentionally curated: tiny fish, Gasopod, held items,
            // and scene-only entities are omitted so every button represents a usable
            // third-person form.
            Forms.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        private void Awake()
        {
            Instance = this;
            CompleteCatalogue();
            menuKey = Config.Bind("Controls", "Menu", KeyCode.F6, "Open the transformation menu.");
            returnKey = Config.Bind("Controls", "Human", KeyCode.F7, "Return to the human form.");
            attackKey = Config.Bind("Controls", "Attack", KeyCode.Mouse0, "Use the selected attack.");
            attackCycleKey = Config.Bind("Controls", "NextAttack", KeyCode.R, "Cycle the available attacks.");
            viewKey = Config.Bind("Controls", "ChangeView", KeyCode.V, "Toggle first and third person.");
            sizeMultiplier = Config.Bind("Creatures", "SizeMultiplier", 1f, new ConfigDescription("Visual size multiplier.", new AcceptableValueRange<float>(.25f, 2f)));
            soundVolume = Config.Bind("Creatures", "SoundVolume", 1f, new ConfigDescription("Native creature sound volume.", new AcceptableValueRange<float>(0f, 1.5f)));
            unlimitedOxygen = Config.Bind("Creatures", "AquaticBreathing", true, "Breathe underwater while morphed.");
            harmony = new Harmony(Id);
            harmony.PatchAll(typeof(Plugin).Assembly);
            Camera.onPreCull += BeforeCameraRender;
            Camera.onPostRender += AfterCameraRender;
            Logger.LogInfo("CreatureMorphBeta 2.1.2 ready — F6: menu, F7: human, R: next attack.");
        }

        private bool CanTransform(Player player)
        {
            return player != null && player.IsAlive()
                && player.GetVehicle() == null && !player.isPiloting && !player.cinematicModeActive
                && !player.GetPDA().isInUse;
        }

        private void Update()
        {
            // Also recovers a render interrupted before onPostRender.
            RestoreCamera();
            if (owner != null && (Player.main != owner || !CanTransform(owner))) ResetForm();
            if (visual != null && owner == null) ResetForm();
            if (Player.main == null) { if (MenuOpen) SetMenu(false); return; }
            if (Input.GetKeyDown(returnKey.Value)) { ResetForm(); SetMenu(false); }
            if (Input.GetKeyDown(menuKey.Value) && Time.timeScale > 0 && !Player.main.GetPDA().isInUse)
                SetMenu(!MenuOpen);
            if (MenuOpen && Input.GetKeyDown(KeyCode.Escape)) SetMenu(false);
            if (Time.timeScale <= 0) { if (MenuOpen) SetMenu(false); return; }
            if (!Morphed || MenuOpen) return;
            if (creatureSounds != null && Time.time >= nextAmbientSound && Time.time >= morphStarted + 1.2f)
            {
                creatureSounds.PlayAmbient(owner.transform.position, unitySoundSource, soundVolume.Value);
                nextAmbientSound = Time.time + UnityEngine.Random.Range(4f, 9f);
            }
            if (Input.GetKeyDown(viewKey.Value)) firstPerson = !firstPerson;
            if (Input.GetKeyDown(attackCycleKey.Value)) CycleAttack();
            float wheel = Input.mouseScrollDelta.y;
            if (!firstPerson && Mathf.Abs(wheel) > .001f)
                zoomDistance = CameraPolicy.Zoom(zoomDistance, wheel, length);
            if (Time.time < morphStarted + 1.2f) return;
            if (Input.GetKeyDown(attackKey.Value) && Time.time >= readyAt)
            {
                readyAt = Time.time + Mathf.Max(form.Cooldown, AttackDuration());
                attackUntil = Time.time + AttackDuration();
                hitAt = Time.time + .23f;
                hitPending = true;
                foreach (AnimationRig rig in rigs) rig.StartAttack();
                if (creatureSounds != null) creatureSounds.PlayAttack(owner.transform.position, unitySoundSource, soundVolume.Value);
                ExecuteAttack();
            }
            if (hitPending && Time.time >= hitAt && attackMode == AttackMode.Primary) { hitPending = false; DealDamage(); }
        }

        private AttackMode[] AvailableAttacks()
        {
            if (form == null) return new [] { AttackMode.Primary };
            if (form.Tech == TechType.SeaDragon) return new [] { AttackMode.Primary, AttackMode.Fireball };
            if (form.Tech == TechType.Warper) return new [] { AttackMode.Primary, AttackMode.Teleport };
            if (form.Tech == TechType.CrabSquid) return new [] { AttackMode.Primary, AttackMode.EmpPulse };
            return new [] { AttackMode.Primary };
        }

        private string AttackName(AttackMode mode)
        {
            switch (mode)
            {
                case AttackMode.Fireball: return "Fireball";
                case AttackMode.Teleport: return "Teleport";
                case AttackMode.EmpPulse: return "EMP Pulse";
                default: return "Primary Attack";
            }
        }

        private void CycleAttack()
        {
            AttackMode[] modes = AvailableAttacks();
            int index = Array.IndexOf(modes, attackMode);
            attackMode = modes[(index + 1 + modes.Length) % modes.Length];
            status = "Attack selected: " + AttackName(attackMode) + ".";
        }

        private void ExecuteAttack()
        {
            try
            {
                switch (attackMode)
                {
                    case AttackMode.Fireball: FireFireball(); break;
                    case AttackMode.Teleport: TeleportForward(); hitPending = false; break;
                    case AttackMode.EmpPulse: EmpPulse(); hitPending = false; break;
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Special attack failed: " + e);
                attackMode = AttackMode.Primary;
                hitPending = true;
            }
        }

        private void FireFireball()
        {
            SeaDragonMeleeAttack melee = sourcePrefab == null ? null : sourcePrefab.GetComponentInChildren<SeaDragonMeleeAttack>(true);
            RangedAttackLastTarget ranged = melee == null ? null : melee.rangedAttackLastTarget;
            if (ranged == null || ranged.attackTypes == null)
            { Logger.LogWarning("Sea Dragon fireball data is unavailable; using the primary attack."); attackMode = AttackMode.Primary; hitPending = true; return; }
            RangedAttackLastTarget.RangedAttackType chosen = null;
            foreach (RangedAttackLastTarget.RangedAttackType type in ranged.attackTypes)
                if (type != null && type.ammoPrefab != null) { chosen = type; break; }
            if (chosen == null)
            { Logger.LogWarning("Sea Dragon has no projectile prefab; using the primary attack."); attackMode = AttackMode.Primary; hitPending = true; return; }
            Transform spawn = ranged.ammoSpawnPoint != null ? ranged.ammoSpawnPoint : owner.transform;
            Vector3 direction = MainCamera.camera == null ? owner.transform.forward : MainCamera.camera.transform.forward;
            GameObject projectileObject = Instantiate(chosen.ammoPrefab, spawn.position, Quaternion.LookRotation(direction));
            Projectile projectile = projectileObject.GetComponentInChildren<Projectile>();
            if (projectile == null) { Destroy(projectileObject, 5); Logger.LogWarning("Sea Dragon projectile has no Projectile component."); return; }
            projectile.damage = Mathf.Max(projectile.damage, actualDamage);
            projectile.Shoot(direction * Mathf.Max(1, chosen.ammoVelocity));
        }

        private void TeleportForward()
        {
            Vector3 origin = owner.transform.position;
            Vector3 direction = MainCamera.camera == null ? owner.transform.forward : MainCamera.camera.transform.forward;
            float distance = 14f;
            RaycastHit hit;
            if (Physics.SphereCast(origin, .45f, direction, out hit, distance, ~0, QueryTriggerInteraction.Ignore)
                && hit.collider != null && !hit.collider.transform.IsChildOf(owner.transform)) distance = Mathf.Max(2, hit.distance - .7f);
            Vector3 destination = origin + direction.normalized * distance;
            SpawnWarpEffect(origin, false);
            owner.SetPosition(destination, owner.transform.rotation);
            SpawnWarpEffect(destination, true);
            lastPosition = destination;
        }

        private void SpawnWarpEffect(Vector3 position, bool inbound)
        {
            Warper warper = sourcePrefab == null ? null : sourcePrefab.GetComponentInChildren<Warper>(true);
            GameObject effect = warper == null ? null : (inbound ? warper.warpInEffectPrefab : warper.warpOutEffectPrefab);
            if (effect != null) Destroy(Instantiate(effect, position, Quaternion.identity), 5f);
        }

        private void EmpPulse()
        {
            Vector3 center = owner.transform.position;
            float radius = Mathf.Clamp(length * .8f, 4, 18);
            foreach (Collider collider in Physics.OverlapSphere(center, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                LiveMixin life = collider.GetComponentInParent<LiveMixin>();
                if (life != null && life != owner.liveMixin && life.IsAlive()) life.TakeDamage(Mathf.Max(10, actualDamage * .35f), collider.ClosestPoint(center), DamageType.Electrical, owner.gameObject);
            }
        }

        private void LateUpdate()
        {
            if (MenuOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (!Morphed) return;
            if (Time.unscaledTime >= nextRendererRefresh)
            {
                playerRenderers = owner.GetComponentsInChildren<Renderer>(true);
                nextRendererRefresh = Time.unscaledTime + 1;
            }
            foreach (Renderer renderer in playerRenderers)
            {
                if (renderer == null) continue;
                if (!hidden.ContainsKey(renderer)) hidden.Add(renderer, renderer.enabled);
                renderer.enabled = false;
            }
            Camera camera = MainCamera.camera;
            if (camera == null) return;
            Vector3 velocity = (owner.transform.position - lastPosition) / Mathf.Max(Time.deltaTime, .001f);
            float speed = velocity.magnitude;
            lastPosition = owner.transform.position;
            UpdateGroundContact();
            float phase = Mathf.Clamp01((attackUntil - Time.time) / AttackDuration());
            float progress = Mathf.Clamp01((Time.time - morphStarted) / 1.2f);
            float eased = progress * progress * (3 - 2 * progress);
            visual.transform.localScale = fullScale * Mathf.Lerp(.02f, 1f, eased);
            float lunge = Mathf.Sin(phase * Mathf.PI) * Mathf.Min(length * .12f, 1.5f);
            Quaternion target = camera.transform.rotation;
            if (grounded)
            {
                Vector3 forward = Vector3.ProjectOnPlane(camera.transform.forward, groundNormal);
                if (forward.sqrMagnitude > .001f) target = Quaternion.LookRotation(forward.normalized, groundNormal);
            }
            // A visible whole-body strike also works for species without a bite animation.
            target *= Quaternion.Euler(-Mathf.Sin(phase * Mathf.PI) * 12f, 0, grounded ? 0 : Mathf.Sin(Time.time * 2f) * Mathf.Clamp(speed, 0, 3));
            target *= Quaternion.Euler(0, (1 - eased) * 360f, 0);
            visual.transform.rotation = Quaternion.Slerp(visual.transform.rotation, target, Time.deltaTime * 8);
            // Creature origins vary wildly between prefabs. Keep the actual visible
            // body centred on the player's position so small models cannot end up
            // behind the camera or inside the terrain.
            visual.transform.position = owner.transform.position + camera.transform.forward * lunge
                + Vector3.up * (grounded ? groundLift : 0) - visual.transform.rotation * modelCenter;
            Vector3 localVelocity = visual.transform.InverseTransformDirection(velocity);
            foreach (AnimationRig rig in rigs) rig.Tick(speed, Time.deltaTime, grounded, localVelocity);
        }

        private bool WalksOnGround()
        {
            return form != null && (form.Tech == TechType.CrabSquid || form.Tech == TechType.SeaTreader
                || form.Tech == TechType.CaveCrawler || form.Tech == TechType.PrecursorDroid
                || form.Tech == TechType.LavaLizard || form.Tech == TechType.Shuttlebug || form.Tech == TechType.Grabcrab);
        }

        private void UpdateGroundContact()
        {
            if (!WalksOnGround()) { grounded = false; return; }
            if (Time.time < nextGroundProbe) return;
            nextGroundProbe = Time.time + .1f;
            Vector3 origin = owner.transform.position + Vector3.up * .5f;
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, castBuffer, grounded ? 3.5f : 2.8f, ~0, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            grounded = false;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = castBuffer[i];
                if (hit.collider == null || hit.collider.transform.IsChildOf(owner.transform) || hit.normal.y < .45f) continue;
                if (hit.collider.GetComponentInParent<Creature>() != null) continue;
                if (hit.distance >= nearest) continue;
                nearest = hit.distance; grounded = true; groundNormal = hit.normal;
                groundLift = hit.point.y - owner.transform.position.y - modelBottom;
            }
        }

        private void SetMenu(bool open)
        {
            if (open == MenuOpen) return;
            if (open)
            {
                previousLock = Cursor.lockState; previousCursor = Cursor.visible; hitPending = false;
                UWE.Utils.PushLockCursor(false); cursorPushed = true;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            }
            else
            {
                if (cursorPushed) { UWE.Utils.PopLockCursor(); cursorPushed = false; }
                Cursor.lockState = previousLock; Cursor.visible = previousCursor;
            }
            MenuOpen = open;
        }

        private void OnGUI()
        {
            if (Player.main == null) return;
            EnsureStyles();
            if (MenuOpen)
            {
                window.width = Mathf.Min(520, Screen.width - 20);
                // Keep the action buttons inside the window. The longer creature
                // catalogue must scroll instead of pushing TRANSFORM off-screen.
                window.height = Mathf.Min(760, Screen.height - 20);
                window.x = Mathf.Clamp(window.x, 0, Screen.width - window.width);
                window.y = Mathf.Clamp(window.y, 0, Screen.height - window.height);
                window = GUI.Window(791326, window, DrawMenu, "CREATURE MORPH  |  TRANSFORMATION");
            }
            else if (Morphed)
                GUI.Box(new Rect(15, 15, 620, 116), form.Name + "  |  " + menuKey.Value + " menu  |  " + returnKey.Value + " human\n"
                    + attackKey.Value + " attack: " + AttackName(attackMode) + "  •  " + (Time.time < morphStarted + 1.2f ? "Transforming…" : Time.time >= readyAt ? "Ready" : "Cooldown…")
                    + "\nHP: " + Mathf.CeilToInt(owner.liveMixin.health) + " / " + Mathf.CeilToInt(owner.liveMixin.maxHealth)
                    + "\n" + viewKey.Value + " view  •  " + attackCycleKey.Value + " next attack  •  " + (firstPerson ? "First person" : "Mouse wheel: camera distance"));
        }

        private void EnsureStyles()
        {
            if (menuTitle != null) return;
            menuTitle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            menuTitle.normal.textColor = new Color(.35f, .9f, 1f);
            menuSubtitle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            menuSubtitle.normal.textColor = new Color(.75f, .9f, 1f);
            menuButton = new GUIStyle(GUI.skin.button) { fontSize = 14, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(16, 10, 7, 7) };
            menuSelected = new GUIStyle(menuButton) { fontStyle = FontStyle.Bold };
            menuAction = new GUIStyle(GUI.skin.button) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(10, 10, 9, 9) };
            menuSmall = new GUIStyle(GUI.skin.textField) { fontSize = 14, padding = new RectOffset(10, 8, 7, 7) };
            menuBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 12, 12) };
        }

        private string AttackSummary(TechType tech)
        {
            if (tech == TechType.SeaDragon) return "Primary attack  •  Fireball";
            if (tech == TechType.Warper) return "Primary attack  •  Teleport";
            if (tech == TechType.CrabSquid) return "Primary attack  •  EMP pulse";
            return "Primary attack";
        }

        private void DrawMenu(int id)
        {
            GUILayout.BeginVertical(menuBox);
            GUILayout.Label("CREATURE MORPH", menuTitle);
            GUILayout.Label("Choose a large playable creature and keep its original animations.", menuSubtitle, GUILayout.Height(32));
            GUILayout.Space(5);
            GUI.backgroundColor = new Color(.04f, .18f, .28f, .95f);
            GUILayout.BeginVertical("box");
            GUILayout.Label("CONTROLS", menuSubtitle);
            GUILayout.Label("F6 menu   |   F7 human   |   " + attackKey.Value + " attack   |   " + attackCycleKey.Value + " cycle attack   |   " + viewKey.Value + " view", menuSubtitle);
            GUILayout.EndVertical();
            GUI.backgroundColor = Color.white;
            GUILayout.Label(status, menuSubtitle, GUILayout.Height(30));
            search = GUILayout.TextField(search, menuSmall, GUILayout.Height(32));
            GUILayout.Label(selected == null ? "Select a creature below." : "Selected: " + selected.Name, menuSubtitle);
            GUI.enabled = selected != null && !loading;
            GUI.backgroundColor = new Color(.1f, .65f, .85f);
            if (GUILayout.Button("TRANSFORM", menuAction, GUILayout.Height(42)))
            {
                if (!CanTransform(Player.main)) status = "Close the PDA and leave a vehicle or cinematic before transforming.";
                else StartCoroutine(TransformInto(selected));
            }
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            GUILayout.Label("AVAILABLE FORMS  •  " + Forms.Count, menuSubtitle);
            float reservedActions = Morphed ? 495f : 425f;
            float formListHeight = Mathf.Clamp(window.height - reservedActions, 150f, 330f);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(formListHeight));
            GUI.enabled = !loading;
            foreach (Form candidate in Forms)
            {
                if (search.Length > 0 && (candidate.Name + candidate.Tech).IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool isSelected = selected == candidate;
                GUI.backgroundColor = isSelected ? new Color(.1f, .55f, .75f) : new Color(.09f, .24f, .32f);
                if (GUILayout.Button((isSelected ? "◆  " : "   ") + candidate.Name + "   [" + AttackSummary(candidate.Tech) + "]", isSelected ? menuSelected : menuButton, GUILayout.Height(34)))
                { selected = candidate; status = unavailable.ContainsKey(candidate.Tech) ? unavailable[candidate.Tech] : candidate.Name + " selected. Press Transform."; }
            }
            GUI.enabled = true;
            GUI.backgroundColor = Color.white;
            GUILayout.EndScrollView();
            if (selected != null)
            {
                GUI.backgroundColor = new Color(.04f, .18f, .28f, .95f);
                GUILayout.BeginVertical("box");
                GUILayout.Label("SELECTED  •  " + selected.Name, menuSubtitle);
                GUILayout.Label(AttackSummary(selected.Tech), menuSubtitle);
                GUILayout.EndVertical();
                GUI.backgroundColor = Color.white;
            }
            if (Morphed && GUILayout.Button(firstPerson ? "Switch to third person" : "Switch to first person", menuButton, GUILayout.Height(32))) firstPerson = !firstPerson;
            if (Morphed && GUILayout.Button("Change attack type  •  " + AttackName(attackMode), menuAction, GUILayout.Height(38))) CycleAttack();
            if (GUILayout.Button("Return to human", menuButton, GUILayout.Height(32))) ResetForm();
            if (GUILayout.Button("Close", menuButton, GUILayout.Height(32))) SetMenu(false);
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, window.width, 22));
        }

        private IEnumerator TransformInto(Form next)
        {
            int token = ++generation;
            Player requestedPlayer = Player.main;
            loading = true;
            status = "Loading: " + next.Name;
            var result = new ModelResult();
            yield return GuardedLoad(LoadModel(next.Tech, result, token), result, token);
            if (token != generation) yield break;
            if (requestedPlayer != Player.main || !CanTransform(requestedPlayer))
            { loading = false; status = "Transformation cancelled: player is unavailable."; yield break; }
            if (result.Model == null)
            {
                loading = false;
                status = result.Error ?? "Model unavailable in this game version.";
                unavailable[next.Tech] = status;
                Logger.LogWarning(next.Tech + " : " + status);
                yield break;
            }
            unavailable.Remove(next.Tech);
            ResetForm();
            token = generation;
            try
            {
                GameObject prefab = result.Model;
                sourcePrefab = prefab;
                Grabcrab grab = prefab.GetComponent<Grabcrab>();
                GameObject body = grab != null && grab.model != null ? grab.model : prefab;
                visual = BuildVisual(body);
                creatureSounds = CreatureSoundBank.FromPrefab(prefab);
                Logger.LogInfo(next.Tech + ": native sound events collected = " + creatureSounds.NativeEventCount);
                unitySoundSource = visual.AddComponent<AudioSource>();
                unitySoundSource.playOnAwake = false;
                unitySoundSource.loop = false;
                unitySoundSource.spatialBlend = 1f;
                unitySoundSource.dopplerLevel = 0f;
                unitySoundSource.minDistance = Mathf.Max(2f, length * .15f);
                unitySoundSource.maxDistance = Mathf.Max(30f, length * 2f);
                if (body != prefab) visual.transform.localScale = body.transform.lossyScale;
                owner = requestedPlayer;
                form = next;
                visual.transform.position = owner.transform.position;
                visual.transform.rotation = Quaternion.identity;
                lastPosition = owner.transform.position;
                animators = visual.GetComponentsInChildren<Animator>(false);
                visual.SetActive(true);
                foreach (Animator animator in animators)
                {
                    AnimationClip[] clips = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.animationClips : new AnimationClip[0];
                    if (clips.Length > 0) rigs.Add(new AnimationRig(animator, clips));
                    Logger.LogInfo(next.Tech + " animations : " + string.Join(", ", Array.ConvertAll(clips, c => c.name)));
                }
                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                Bounds bounds = new Bounds();
                bool first = true;
                foreach (Renderer renderer in renderers)
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy) { if (first) { bounds = renderer.bounds; first = false; } else bounds.Encapsulate(renderer.bounds); }
                if (first) throw new InvalidOperationException("No visible renderers were found.");
                float extent = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
                if (float.IsNaN(extent) || float.IsInfinity(extent) || extent <= 0)
                    throw new InvalidOperationException("Invalid model dimensions: " + next.Tech);
                // Preserve the prefab's original scale. Bounds are used only for camera framing.
                visual.transform.localScale *= sizeMultiplier.Value;
                length = Mathf.Max(.1f, extent * sizeMultiplier.Value);
                if (unitySoundSource != null)
                {
                    unitySoundSource.minDistance = Mathf.Max(2f, length * .15f);
                    unitySoundSource.maxDistance = Mathf.Max(30f, length * 2f);
                }
                modelBottom = (bounds.min.y - bounds.center.y) * sizeMultiplier.Value;
                fullScale = visual.transform.localScale;
                creatureRenderers = Array.FindAll(renderers, r => r.enabled && r.gameObject.activeInHierarchy);
                renderHidden = new bool[creatureRenderers.Length];
                Vector3 scaledCenter = owner.transform.position + (bounds.center - owner.transform.position) * sizeMultiplier.Value;
                modelCenter = visual.transform.InverseTransformPoint(scaledCenter);
                eyeLocal = visual.transform.InverseTransformPoint(scaledCenter + Vector3.forward * bounds.extents.z * sizeMultiplier.Value * .85f);
                foreach (Transform bone in visual.GetComponentsInChildren<Transform>(false))
                {
                    string boneName = bone.name.ToLowerInvariant();
                    if (boneName == "head" || boneName.EndsWith("_head") || boneName == "head_jnt")
                    { eyeLocal = visual.transform.InverseTransformPoint(bone.position); break; }
                }
                zoomDistance = CameraPolicy.DefaultDistance(length, MainCamera.camera.fieldOfView);
                firstPerson = false;
                morphStarted = Time.time;
                visual.transform.localScale = fullScale * .02f;
                nextAmbientSound = Time.time + 1.4f;
                ApplyCreatureHealth(prefab);
                MeleeAttack melee = prefab.GetComponentInChildren<MeleeAttack>(true);
                actualDamage = melee != null && melee.biteDamage > 0 ? melee.biteDamage : next.Damage;
                attackMode = AttackMode.Primary;
                loading = false;
                status = next.Name + " selected.";
                Logger.LogInfo(next.Tech + ": native scale " + fullScale + ", bounds " + length);
                Logger.LogInfo(next.Tech + " : active renderers " + creatureRenderers.Length + ", model centre " + modelCenter);
                SetMenu(false);
            }
            catch (Exception e)
            {
                Logger.LogWarning("Transformation failed for: " + next.Tech);
                Fail(e, token);
                status = "Transformation failed: " + next.Name + ". " + e.Message;
            }
        }

        private sealed class ModelResult { public GameObject Model; public string Error; }

        private IEnumerator GuardedLoad(IEnumerator loader, ModelResult result, int token)
        {
            var pending = new Stack<IEnumerator>();
            pending.Push(loader);
            float deadline = Time.realtimeSinceStartup + 45;
            while (pending.Count > 0 && token == generation)
            {
                if (Time.realtimeSinceStartup > deadline) { result.Error = "Loading stopped after 45 seconds. Try again after the area finishes loading."; yield break; }
                bool more;
                object current = null;
                try { more = pending.Peek().MoveNext(); if (more) current = pending.Peek().Current; }
                catch (Exception e) { result.Error = "Loading failed: " + e.Message; Logger.LogWarning(e); yield break; }
                if (!more) { pending.Pop(); continue; }
                IEnumerator nested = current as IEnumerator;
                if (nested != null && nested != pending.Peek()) { pending.Push(nested); continue; }
                yield return null;
            }
        }

        private IEnumerator LoadModel(TechType tech, ModelResult result, int token)
        {
            string classId = CraftData.GetClassIdForTechType(tech);
            if (!string.IsNullOrEmpty(classId))
            {
                yield return ReadRequest(UWE.PrefabDatabase.GetPrefabAsync(classId), result, token);
                if (token != generation || result.Model != null) yield break;
            }
            // Some fauna are addressable prefabs without an EntTechData mapping.
            string file = null;
            if (UWE.PrefabDatabase.prefabFiles != null) foreach (KeyValuePair<string, string> entry in UWE.PrefabDatabase.prefabFiles)
            {
                string path = entry.Value.Replace('\\', '/');
                if (path.IndexOf("/Creatures/", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), tech.ToString(), StringComparison.OrdinalIgnoreCase))
                { file = entry.Value; break; }
            }
            if (file == null) PrefabPaths.Files.TryGetValue(tech.ToString(), out file);
            if (file != null)
            {
                yield return ReadRequest(UWE.PrefabDatabase.GetPrefabForFilenameAsync(file), result, token);
                if (token != generation || result.Model != null) yield break;
            }
            // The adult emperor is a scene entity, not a freely spawnable TechType prefab.
            if (tech == TechType.SeaEmperorLeviathan || tech == TechType.SeaEmperor)
            {
                foreach (SeaEmperor emperor in Resources.FindObjectsOfTypeAll<SeaEmperor>())
                    if (emperor != null && emperor.GetComponentsInChildren<Renderer>(true).Length > 0)
                    { result.Model = emperor.gameObject; yield break; }
                result.Error = "Adult emperor: approach its aquarium to load the model, then try again.";
            }
            else if (result.Error == null) result.Error = "Model missing or unavailable: " + tech + ". Your current form was kept.";
        }

        private IEnumerator ReadRequest(UWE.IPrefabRequest request, ModelResult result, int token)
        {
            float deadline = Time.realtimeSinceStartup + 20;
            var stack = new Stack<IEnumerator>();
            stack.Push(request);
            while (stack.Count > 0 && token == generation)
            {
                if (Time.realtimeSinceStartup > deadline) { result.Error = "Loading took too long. Try again after the area has loaded."; yield break; }
                object current = null;
                bool more = false;
                try { more = stack.Peek().MoveNext(); if (more) current = stack.Peek().Current; }
                catch (Exception e) { result.Error = e.Message; Logger.LogWarning(e); yield break; }
                if (!more) { stack.Pop(); continue; }
                IEnumerator nested = current as IEnumerator;
                if (nested != null) { stack.Push(nested); continue; }
                AsyncOperation operation = current as AsyncOperation;
                while (operation != null && !operation.isDone && token == generation && Time.realtimeSinceStartup <= deadline) yield return null;
                yield return null;
            }
            if (token != generation) yield break;
            try { request.TryGetPrefab(out result.Model); }
            catch (Exception e) { result.Error = e.Message; }
        }

        private void Fail(Exception exception, int token)
        {
            if (token != generation) return;
            ResetForm();
            status = "Loading failed. See BepInEx/LogOutput.log for details.";
            Logger.LogError(exception);
        }

        private void ApplyCreatureHealth(GameObject prefab)
        {
            LiveMixin native = prefab.GetComponent<LiveMixin>();
            if (native == null) native = prefab.GetComponentInChildren<LiveMixin>(true);
            if (native == null || native.data == null || native.maxHealth <= 0)
            {
                // Some story entities cannot take damage and have no health pool to copy.
                Logger.LogWarning(form.Tech + ": original health data unavailable; human health retained.");
                return;
            }
            healthTarget = owner.liveMixin;
            originalHealthData = healthTarget.data;
            originalHealth = healthTarget.health;
            originalTempDamage = healthTarget.tempDamage;
            // Clone only the player's data. Never edit the shared creature/player ScriptableObject.
            morphHealthData = Instantiate(originalHealthData);
            morphHealthData.maxHealth = native.maxHealth;
            healthTarget.data = morphHealthData;
            healthTarget.health = native.maxHealth;
            healthTarget.tempDamage = 0;
            Logger.LogInfo(form.Tech + ": original health = " + native.maxHealth);
        }

        private void RestoreHealth()
        {
            if (healthTarget != null && originalHealthData != null && healthTarget.data == morphHealthData)
            {
                float restored = HealthPolicy.RestoredHealth(originalHealth, originalHealthData.maxHealth, healthTarget.health, morphHealthData.maxHealth);
                healthTarget.data = originalHealthData;
                // Damage taken as a creature is retained proportionally, with no resurrection.
                healthTarget.health = restored;
                healthTarget.tempDamage = originalTempDamage;
            }
            if (morphHealthData != null) Destroy(morphHealthData);
            healthTarget = null; originalHealthData = null; morphHealthData = null;
        }

        private float AttackDuration()
        {
            float duration = .65f;
            foreach (AnimationRig rig in rigs) duration = Mathf.Max(duration, rig.AttackLength);
            return Mathf.Min(duration, 3);
        }

        // Play actual clips directly: controller state names differ between every species.
        private sealed class AnimationRig : IDisposable
        {
            private PlayableGraph graph;
            private AnimationMixerPlayable mixer;
            private readonly AnimationClipPlayable[] players = new AnimationClipPlayable[9];
            private readonly AnimationClip[] bank = new AnimationClip[9];
            private float clock, attackClock = 1000, groundWeight;
            public float AttackLength { get { return Mathf.Clamp(Mathf.Max(bank[7] == null ? 0 : bank[7].length, bank[8] == null ? 0 : bank[8].length), .65f, 3); } }

            public AnimationRig(Animator animator, AnimationClip[] clips)
            {
                var supported = new List<AnimationClip>();
                foreach (AnimationClip clip in clips) if (clip != null && !clip.legacy) supported.Add(clip);
                clips = supported.ToArray();
                string[] names = Array.ConvertAll(clips, c => c.name);
                bank[0] = Pick(clips, names, false, true, false, "forward");
                bank[1] = Pick(clips, names, false, false, false, "forward");
                bank[2] = Pick(clips, names, true, true, false, "forward");
                bank[3] = Pick(clips, names, true, false, false, "forward");
                bank[4] = Pick(clips, names, true, false, false, "back");
                bank[5] = Pick(clips, names, true, false, false, "left");
                bank[6] = Pick(clips, names, true, false, false, "right");
                bank[7] = Pick(clips, names, false, false, true, "forward");
                bank[8] = Pick(clips, names, true, false, true, "forward");
                AnimationClip fallback = bank[0] ?? bank[1] ?? bank[2] ?? bank[3];
                if (fallback == null) return;
                for (int i = 0; i < 7; i++) if (bank[i] == null) bank[i] = fallback;
                graph = PlayableGraph.Create("CreatureMorph_" + animator.name);
                graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                mixer = AnimationMixerPlayable.Create(graph, bank.Length);
                for (int i = 0; i < bank.Length; i++)
                {
                    players[i] = AnimationClipPlayable.Create(graph, bank[i] ?? fallback);
                    graph.Connect(players[i], 0, mixer, i);
                }
                var output = AnimationPlayableOutput.Create(graph, "Body", animator);
                output.SetSourcePlayable(mixer);
                animator.applyRootMotion = false; animator.fireEvents = false;
                graph.Play();
                Tick(0, 0, false, Vector3.zero);
                Instance.Logger.LogInfo("Clips choisis : nage=" + bank[1].name + ", marche=" + bank[3].name + ", repos au sol=" + bank[2].name);
            }
            private static AnimationClip Pick(AnimationClip[] clips, string[] names, bool ground, bool idle, bool attack, string direction)
            {
                int index = AnimationPolicy.Select(names, ground, idle, attack, direction);
                return index < 0 ? null : clips[index];
            }
            public void StartAttack() { attackClock = 0; }
            public void Tick(float speed, float dt, bool grounded, Vector3 localVelocity)
            {
                if (!graph.IsValid()) return;
                clock += dt * Mathf.Clamp(.7f + speed * .12f, .7f, 1.8f);
                attackClock += dt;
                groundWeight = Mathf.MoveTowards(groundWeight, grounded ? 1 : 0, dt * 4);
                int groundMove = Mathf.Abs(localVelocity.x) > Mathf.Abs(localVelocity.z) ? (localVelocity.x < 0 ? 5 : 6) : localVelocity.z < -.1f ? 4 : 3;
                int attackIndex = grounded ? 8 : 7;
                AnimationClip attackClip = bank[attackIndex];
                float duration = attackClip == null ? .65f : Mathf.Min(3, attackClip.length);
                float attackWeight = attackClip == null ? 0 : Mathf.Clamp01(Mathf.Min(attackClock / .1f, (duration - attackClock) / .15f));
                float horizontalSpeed = new Vector2(localVelocity.x, localVelocity.z).magnitude;
                float moving = Mathf.Clamp01((grounded ? horizontalSpeed : speed) / .5f);
                for (int i = 0; i < bank.Length; i++)
                {
                    mixer.SetInputWeight(i, 0);
                    players[i].SetTime(i >= 7 ? Mathf.Min(attackClock, duration) : clock % Mathf.Max(.01f, bank[i].length));
                }
                mixer.SetInputWeight(0, (1 - attackWeight) * (1 - moving) * (1 - groundWeight));
                mixer.SetInputWeight(1, (1 - attackWeight) * moving * (1 - groundWeight));
                mixer.SetInputWeight(2, (1 - attackWeight) * (1 - moving) * groundWeight);
                mixer.SetInputWeight(groundMove, (1 - attackWeight) * moving * groundWeight);
                mixer.SetInputWeight(attackIndex, attackWeight);
                graph.Evaluate(0);
            }
            public void Dispose() { if (graph.IsValid()) graph.Destroy(); }
        }

        // Creature prefabs use FMOD emitters instead of Unity AudioSources for
        // most voices. Collect their native events without copying AI or damage
        // components, then play the events from the player's creature position.
        private sealed class CreatureSoundBank
        {
            private readonly List<FMODAsset> allEvents = new List<FMODAsset>();
            private readonly List<FMODAsset> ambientEvents = new List<FMODAsset>();
            private readonly List<FMODAsset> attackEvents = new List<FMODAsset>();
            private readonly List<string> ambientPaths = new List<string>();
            private readonly List<string> attackPaths = new List<string>();
            private readonly List<AudioClip> allClips = new List<AudioClip>();
            private readonly List<AudioClip> ambientClips = new List<AudioClip>();
            private readonly List<AudioClip> attackClips = new List<AudioClip>();
            private readonly HashSet<string> eventKeys = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> pathKeys = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<int> clipKeys = new HashSet<int>();
            private readonly List<FMOD.Studio.EventInstance> activeInstances = new List<FMOD.Studio.EventInstance>();
            private readonly List<FMOD_StudioEventEmitter> activeEmitters = new List<FMOD_StudioEventEmitter>();
            private readonly List<GameObject> activeHosts = new List<GameObject>();

            public int NativeEventCount { get { return allEvents.Count + ambientPaths.Count + attackPaths.Count; } }

            public static CreatureSoundBank FromPrefab(GameObject prefab)
            {
                var bank = new CreatureSoundBank();
                if (prefab == null) return bank;
                foreach (Component component in prefab.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    bool componentAttack = IsAttackName(component.GetType().Name);
                    AudioSource audio = component as AudioSource;
                    if (audio != null) bank.AddClip(audio.clip, componentAttack);
                    FieldInfo[] fields;
                    try { fields = component.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                    catch { continue; }
                    foreach (FieldInfo field in fields)
                    {
                        object value;
                        try { value = field.GetValue(component); }
                        catch { continue; }
                        if (value == null) continue;
                        bank.Collect(value, componentAttack || IsAttackName(field.Name), 2);
                    }
                }
                if (bank.ambientEvents.Count == 0) bank.ambientEvents.AddRange(bank.allEvents);
                if (bank.attackEvents.Count == 0) bank.attackEvents.AddRange(bank.allEvents);
                if (bank.ambientPaths.Count == 0) bank.ambientPaths.AddRange(bank.attackPaths);
                if (bank.attackPaths.Count == 0) bank.attackPaths.AddRange(bank.ambientPaths);
                if (bank.ambientClips.Count == 0) bank.ambientClips.AddRange(bank.allClips);
                if (bank.attackClips.Count == 0) bank.attackClips.AddRange(bank.allClips);
                return bank;
            }

            public void PlayAmbient(Vector3 position, AudioSource unitySource, float volume)
            {
                if (!PlayEvent(ambientEvents, position, volume) && !PlayPath(ambientPaths, position, volume)) PlayClip(ambientClips, unitySource, volume);
            }

            public void PlayAttack(Vector3 position, AudioSource unitySource, float volume)
            {
                if (!PlayEvent(attackEvents, position, volume) && !PlayPath(attackPaths, position, volume)) PlayClip(attackClips, unitySource, volume);
            }

            public void StopAll()
            {
                foreach (FMOD_StudioEventEmitter emitter in activeEmitters)
                {
                    if (emitter == null) continue;
                    try { emitter.Stop(true); } catch { }
                    if (emitter.gameObject != null) UnityEngine.Object.Destroy(emitter.gameObject);
                }
                activeEmitters.Clear();
                foreach (FMOD.Studio.EventInstance instance in activeInstances)
                {
                    try { instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); } catch { }
                    try { instance.release(); } catch { }
                }
                activeInstances.Clear();
                foreach (GameObject host in activeHosts) if (host != null) UnityEngine.Object.Destroy(host);
                activeHosts.Clear();
            }

            private bool PlayEvent(List<FMODAsset> events, Vector3 position, float volume)
            {
                if (events.Count == 0) return false;
                FMODAsset sound = events[UnityEngine.Random.Range(0, events.Count)];
                if (sound == null) return false;
                return PlayEmitter(sound, null, position, volume);
            }

            private bool PlayPath(List<string> paths, Vector3 position, float volume)
            {
                if (paths.Count == 0) return false;
                string path = paths[UnityEngine.Random.Range(0, paths.Count)];
                if (String.IsNullOrEmpty(path)) return false;
                return PlayEmitter(null, path, position, volume);
            }

            private bool PlayEmitter(FMODAsset asset, string path, Vector3 position, float volume)
            {
                GameObject host = new GameObject("CreatureMorph_Sound");
                host.transform.position = position;
                string eventPath = path ?? (asset == null ? "" : asset.path);
                FMOD.Studio.EventInstance instance = default(FMOD.Studio.EventInstance);
                bool instanceCreated = false;
                try
                {
                    if (String.IsNullOrEmpty(eventPath)) throw new InvalidOperationException("The creature sound has no FMOD event path.");
                    instance = FMODUnity.RuntimeManager.CreateInstance(eventPath);
                    instanceCreated = true;
                    FMODUnity.RuntimeManager.AttachInstanceToGameObject(instance, host.transform);
                    instance.setVolume(Mathf.Clamp(volume, 0, 2));
                    instance.start();
                    activeInstances.Add(instance);
                    activeHosts.Add(host);
                    UnityEngine.Object.Destroy(host, 12f);
                    return true;
                }
                catch (Exception e)
                {
                    if (instanceCreated)
                    {
                        try { instance.stop(FMOD.Studio.STOP_MODE.IMMEDIATE); } catch { }
                        try { instance.release(); } catch { }
                    }
                    FMOD_StudioEventEmitter emitter = host.AddComponent<FMOD_StudioEventEmitter>();
                    emitter.asset = asset;
                    emitter.path = eventPath;
                    emitter.startEventOnAwake = false;
                    emitter.minInterval = 0;
                    try
                    {
                        emitter.StartEvent();
                        activeEmitters.Add(emitter);
                        activeHosts.Add(host);
                        UnityEngine.Object.Destroy(host, 12f);
                        return true;
                    }
                    catch
                    {
                        UnityEngine.Object.Destroy(host);
                        if (Plugin.Instance != null) Plugin.Instance.Logger.LogWarning("Creature sound could not play: " + e.Message);
                        return false;
                    }
                }
            }

            private static void PlayClip(List<AudioClip> clips, AudioSource source, float volume)
            {
                if (source == null || clips.Count == 0) return;
                AudioClip clip = clips[UnityEngine.Random.Range(0, clips.Count)];
                if (clip != null) source.PlayOneShot(clip, Mathf.Clamp(volume, 0, 2));
            }

            private void Collect(object value, bool attack, int depth)
            {
                if (value == null) return;
                FMOD_StudioEventEmitter studio = value as FMOD_StudioEventEmitter;
                if (studio != null) { AddEvent(studio.asset, attack); AddPath(studio.path, attack); return; }
                FMOD_CustomLoopingEmitter looping = value as FMOD_CustomLoopingEmitter;
                if (looping != null)
                {
                    AddEvent(looping.asset, attack); AddEvent(looping.assetStart, attack); AddEvent(looping.assetStop, attack); return;
                }
                FMOD_CustomEmitter custom = value as FMOD_CustomEmitter;
                if (custom != null) { AddEvent(custom.asset, attack); return; }
                FMODAsset asset = value as FMODAsset;
                if (asset != null) { AddEvent(asset, attack); return; }
                FMODAsset[] assets = value as FMODAsset[];
                if (assets != null) { foreach (FMODAsset item in assets) AddEvent(item, attack); return; }
                string path = value as string;
                if (path != null) { AddPath(path, attack); return; }
                AudioClip clip = value as AudioClip;
                if (clip != null) { AddClip(clip, attack); return; }
                AudioClip[] clips = value as AudioClip[];
                if (clips != null) foreach (AudioClip item in clips) AddClip(item, attack);
                if (depth <= 0) return;
                Array array = value as Array;
                if (array != null)
                {
                    foreach (object item in array) Collect(item, attack, depth - 1);
                    return;
                }
                if (value is UnityEngine.Object) return;
                FieldInfo[] fields;
                try { fields = value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { return; }
                foreach (FieldInfo field in fields)
                {
                    object nested;
                    try { nested = field.GetValue(value); }
                    catch { continue; }
                    Collect(nested, attack || IsAttackName(field.Name), depth - 1);
                }
            }

            private void AddEvent(FMODAsset asset, bool attack)
            {
                if (asset == null) return;
                string key = (asset.path ?? "") + "|" + (asset.id ?? "");
                if (key == "|") key = "#" + asset.GetInstanceID();
                if (!eventKeys.Add(key)) return;
                allEvents.Add(asset);
                if (attack) attackEvents.Add(asset); else ambientEvents.Add(asset);
            }

            private void AddPath(string path, bool attack)
            {
                if (String.IsNullOrEmpty(path) || !path.StartsWith("event:/", StringComparison.OrdinalIgnoreCase)) return;
                if (!pathKeys.Add((attack ? "a|" : "n|") + path)) return;
                if (attack) attackPaths.Add(path); else ambientPaths.Add(path);
            }

            private void AddClip(AudioClip clip, bool attack)
            {
                if (clip == null || !clipKeys.Add(clip.GetInstanceID())) return;
                allClips.Add(clip);
                if (attack) attackClips.Add(clip); else ambientClips.Add(clip);
            }

            private static bool IsAttackName(string value)
            {
                string name = (value ?? "").ToLowerInvariant();
                return name.Contains("attack") || name.Contains("bite") || name.Contains("swat")
                    || name.Contains("claw") || name.Contains("stomp") || name.Contains("damage")
                    || name.Contains("hit") || name.Contains("warp");
            }
        }

        // Reconstruct only the render rig: never instantiate AI, damage, loot or save components.
        private GameObject BuildVisual(GameObject prefab)
        {
            GameObject root = new GameObject("CreatureMorph_Visual");
            root.SetActive(false);
            try
            {
                var map = new Dictionary<Transform, Transform>();
                var rendererMap = new Dictionary<Renderer, Renderer>();
                CopyTree(prefab.transform, root.transform, map, true);
                foreach (KeyValuePair<Transform, Transform> pair in map)
                {
                    if (IsHeldModel(pair.Key, prefab.transform)) { pair.Value.gameObject.SetActive(false); continue; }
                    MeshFilter filter = pair.Key.GetComponent<MeshFilter>();
                    if (filter != null) pair.Value.gameObject.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    foreach (Renderer source in pair.Key.GetComponents<Renderer>())
                    {
                        Renderer copy = null;
                        SkinnedMeshRenderer skin = source as SkinnedMeshRenderer;
                        if (skin != null)
                        {
                            SkinnedMeshRenderer target = pair.Value.gameObject.AddComponent<SkinnedMeshRenderer>();
                            target.sharedMesh = skin.sharedMesh;
                            Transform[] bones = skin.bones;
                            Transform[] mapped = new Transform[bones.Length];
                            for (int i = 0; i < bones.Length; i++) if (bones[i] != null && map.ContainsKey(bones[i])) mapped[i] = map[bones[i]];
                            target.bones = mapped;
                            if (skin.rootBone != null && map.ContainsKey(skin.rootBone)) target.rootBone = map[skin.rootBone];
                            target.localBounds = skin.localBounds;
                            target.updateWhenOffscreen = true;
                            if (skin.sharedMesh != null) for (int i = 0; i < skin.sharedMesh.blendShapeCount; i++) target.SetBlendShapeWeight(i, skin.GetBlendShapeWeight(i));
                            copy = target;
                        }
                        else if (source is MeshRenderer) copy = pair.Value.gameObject.AddComponent<MeshRenderer>();
                        if (copy == null) continue;
                        rendererMap[source] = copy;
                        copy.sharedMaterials = source.sharedMaterials;
                        // Prefab renderers may be disabled by LOD/streaming until their original scripts run.
                        copy.enabled = true;
                        Transform active = pair.Value;
                        while (active != null && active != root.transform) { active.gameObject.SetActive(true); active = active.parent; }
                        copy.shadowCastingMode = source.shadowCastingMode;
                        copy.receiveShadows = source.receiveShadows;
                    }
                    Animator original = pair.Key.GetComponent<Animator>();
                    if (original != null)
                    {
                        Animator copy = pair.Value.gameObject.AddComponent<Animator>();
                        copy.avatar = original.avatar;
                        copy.runtimeAnimatorController = original.runtimeAnimatorController;
                        copy.applyRootMotion = false;
                        copy.fireEvents = false;
                        copy.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    }
                }
                foreach (LODGroup original in prefab.GetComponentsInChildren<LODGroup>(true))
                {
                    LOD[] lods = original.GetLODs();
                    int chosen = -1;
                    for (int i = 0; i < lods.Length; i++)
                    {
                        foreach (Renderer source in lods[i].renderers)
                            if (source != null && rendererMap.ContainsKey(source)) { chosen = i; break; }
                        if (chosen >= 0) break;
                    }
                    var visible = new HashSet<Renderer>();
                    if (chosen >= 0) foreach (Renderer source in lods[chosen].renderers) if (source != null) visible.Add(source);
                    foreach (LOD lod in lods) foreach (Renderer source in lod.renderers)
                        if (source != null && rendererMap.ContainsKey(source)) rendererMap[source].enabled = visible.Contains(source);
                }
                // Do not copy SkyApplier or source material property blocks. Those
                // components can carry a streaming/fade value from the original
                // prefab and make a clone disappear or render at the wrong fade.
                return root;
            }
            catch { Destroy(root); throw; }
        }

        private static bool IsHeldModel(Transform node, Transform root)
        {
            for (Transform t = node; t != null; t = t == root ? null : t.parent)
            {
                string name = t.name.ToLowerInvariant();
                if (name.Contains("viewmodel") || name.Contains("heldmodel")) return true;
                Animator animator = t.GetComponent<Animator>();
                if (animator == null || animator.runtimeAnimatorController == null) continue;
                AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
                bool held = clips.Length > 0;
                foreach (AnimationClip clip in clips)
                    if (clip == null || clip.name.IndexOf("hold", StringComparison.OrdinalIgnoreCase) < 0) { held = false; break; }
                if (held) return true;
            }
            return false;
        }

        private static void CopyTree(Transform source, Transform target, Dictionary<Transform, Transform> map, bool root)
        {
            map.Add(source, target);
            target.localPosition = root ? Vector3.zero : source.localPosition;
            target.localRotation = root ? Quaternion.identity : source.localRotation;
            target.localScale = source.localScale;
            target.gameObject.layer = 0;
            if (!root) target.gameObject.SetActive(source.gameObject.activeSelf);
            foreach (Transform child in source)
            {
                GameObject copy = new GameObject(child.name);
                copy.transform.SetParent(target, false);
                CopyTree(child, copy.transform, map, false);
            }
        }

        private void FindAttackState(Animator animator)
        {
            string[] names = { "bite", "attack", "Attack", "Bite", "attack_front", "shove", "Base Layer.bite", "Base Layer.attack", "Base Layer.Attack" };
            foreach (string name in names)
            {
                int hash = Animator.StringToHash(name);
                if (animator.HasState(0, hash)) { attackStates[animator] = hash; break; }
            }
            Logger.LogInfo(form.Name + ": " + animator.name + ", parameters=" + string.Join(",", Array.ConvertAll(animator.parameters, p => p.name)));
        }

        private void TriggerAttack(Animator animator)
        {
            if (animator == null) return;
            int hash;
            if (attackStates.TryGetValue(animator, out hash)) animator.CrossFade(hash, .08f, 0);
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                string n = parameter.name.ToLowerInvariant();
                if (n != "bite" && n != "attack" && n != "attacking" && n != "attack_front" && n != "shove") continue;
                if (parameter.type == AnimatorControllerParameterType.Trigger) animator.SetTrigger(parameter.nameHash);
            }
        }

        private void DealDamage()
        {
            if (!Morphed || MainCamera.camera == null) return;
            Vector3 origin = firstPerson ? ObstacleSafe(owner.transform.position, visual.transform.TransformPoint(eyeLocal), Mathf.Clamp(length * .01f, .025f, .15f)) : owner.transform.position;
            Vector3 forward = MainCamera.camera.transform.forward;
            float range = Mathf.Clamp(length * .55f, 2, 9);
            LiveMixin closest = null;
            Vector3 point = origin;
            float nearest = float.MaxValue;
            foreach (Collider collider in Physics.OverlapSphere(origin, range, ~0, QueryTriggerInteraction.Ignore))
            {
                LiveMixin life = collider.GetComponentInParent<LiveMixin>();
                if (life == null || life == owner.liveMixin || !life.IsAlive() || collider.transform.IsChildOf(owner.transform)) continue;
                Vector3 candidate = collider.ClosestPoint(origin + forward * range);
                Vector3 delta = candidate - origin;
                if (delta.magnitude > range || Vector3.Dot(forward, delta.normalized) < .35f) continue;
                bool blocked = false;
                foreach (RaycastHit hit in Physics.RaycastAll(origin, delta.normalized, delta.magnitude, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform.IsChildOf(owner.transform)) continue;
                    if (hit.collider.GetComponentInParent<LiveMixin>() == life) continue;
                    blocked = true; break;
                }
                if (!blocked && delta.sqrMagnitude < nearest) { nearest = delta.sqrMagnitude; closest = life; point = candidate; }
            }
            if (closest != null) closest.TakeDamage(actualDamage, point, DamageType.Normal, owner.gameObject);
        }

        internal void RestoreCamera()
        {
            if (shiftedCamera != null)
            {
                shiftedCamera.localPosition = cameraLocal;
                shiftedCamera.localRotation = cameraRotation;
            }
            if (renderingCamera != null) renderingCamera.nearClipPlane = savedNearClip;
            if (renderingCamera != null) for (int i = 0; i < creatureRenderers.Length; i++)
                if (creatureRenderers[i] != null) creatureRenderers[i].forceRenderingOff = renderHidden[i];
            shiftedCamera = null;
            renderingCamera = null;
        }

        private void BeforeCameraRender(Camera camera)
        {
            if (!Morphed || camera != MainCamera.camera) return;
            RestoreCamera();
            renderingCamera = camera;
            shiftedCamera = camera.transform;
            cameraLocal = shiftedCamera.localPosition;
            cameraRotation = shiftedCamera.localRotation;
            savedNearClip = camera.nearClipPlane;
            for (int i = 0; i < creatureRenderers.Length; i++)
                if (creatureRenderers[i] != null) renderHidden[i] = creatureRenderers[i].forceRenderingOff;
            try { PositionCamera(camera); }
            catch (Exception e) { RestoreCamera(); Logger.LogError(e); ResetForm(); }
        }

        private void AfterCameraRender(Camera camera) { if (camera == renderingCamera) RestoreCamera(); }

        private void PositionCamera(Camera renderCamera)
        {
            Transform camera = renderCamera.transform;
            // Focus on the creature, not on the human eye position above a small fish.
            Vector3 pivot = visual.transform.TransformPoint(modelCenter);
            bool useFirstPerson = firstPerson && Time.time >= morphStarted + 1.2f;
            if (useFirstPerson)
            {
                Vector3 eye = visual.transform.TransformPoint(eyeLocal);
                Vector3 origin = owner.transform.position;
                camera.position = ObstacleSafe(origin, eye, Mathf.Clamp(length * .01f, .025f, .15f));
                renderCamera.nearClipPlane = .025f;
                // Hide the body only during this camera's render, avoiding the inside of the skull.
                foreach (Renderer renderer in creatureRenderers) if (renderer != null) renderer.forceRenderingOff = true;
                return;
            }
            Vector3 offset = -camera.forward * zoomDistance + camera.up * zoomDistance * .12f;
            camera.position = ObstacleSafe(pivot, pivot + offset, Mathf.Clamp(length * .015f, .025f, .25f));
            Vector3 look = pivot - camera.position;
            if (look.sqrMagnitude > .0001f) camera.rotation = Quaternion.LookRotation(look, camera.up);
            renderCamera.nearClipPlane = Mathf.Min(savedNearClip, Mathf.Clamp(zoomDistance * .025f, .01f, .1f));
        }

        private Vector3 ObstacleSafe(Vector3 pivot, Vector3 desired, float radius)
        {
            Vector3 offset = desired - pivot;
            float distance = offset.magnitude;
            if (distance < .0001f) return pivot;
            int count = Physics.SphereCastNonAlloc(pivot, radius, offset.normalized, castBuffer, distance, ~0, QueryTriggerInteraction.Ignore);
            if (count == castBuffer.Length) distance = CameraPolicy.Minimum(length);
            for (int i = 0; i < count; i++)
                if (castBuffer[i].collider != null && !castBuffer[i].collider.transform.IsChildOf(owner.transform))
                    distance = Mathf.Min(distance, Mathf.Max(CameraPolicy.Minimum(length), castBuffer[i].distance - radius));
            distance = Mathf.Max(CameraPolicy.Minimum(length), distance);
            return pivot + offset.normalized * distance;
        }

        private void ResetForm()
        {
            generation++;
            loading = false;
            hitPending = false;
            attackUntil = readyAt = 0;
            RestoreCamera();
            ResetGameCamera();
            creatureRenderers = new Renderer[0]; renderHidden = new bool[0]; firstPerson = false;
            RestoreHealth();
            foreach (AnimationRig rig in rigs) rig.Dispose();
            rigs.Clear();
            foreach (KeyValuePair<Renderer, bool> entry in hidden) if (entry.Key != null) entry.Key.enabled = entry.Value;
            hidden.Clear();
            playerRenderers = new Renderer[0]; nextRendererRefresh = 0;
            grounded = false; nextGroundProbe = 0; groundLift = modelBottom = 0;
            if (unitySoundSource != null) unitySoundSource.Stop();
            if (creatureSounds != null) creatureSounds.StopAll();
            if (visual != null) { visual.SetActive(false); Destroy(visual); }
            visual = null; owner = null; form = null;
            sourcePrefab = null; creatureSounds = null; unitySoundSource = null; nextAmbientSound = 0;
            attackMode = AttackMode.Primary;
            animators = new Animator[0]; attackStates.Clear();
            status = "Human form. Choose a creature, then press Transform.";
        }

        private void ResetGameCamera()
        {
            try
            {
                if (MainCameraControl.main != null) MainCameraControl.main.ResetCamera();
                if (MainCamera.camera != null) MainCamera.camera.nearClipPlane = .1f;
            }
            catch (Exception e) { Logger.LogWarning("Camera reset: " + e.Message); }
        }

        private void OnDisable() { StopAllCoroutines(); ResetForm(); SetMenu(false); }
        private void OnDestroy()
        {
            RestoreCamera();
            Camera.onPreCull -= BeforeCameraRender; Camera.onPostRender -= AfterCameraRender;
            if (harmony != null) harmony.UnpatchSelf(); Instance = null;
        }

        [HarmonyPatch(typeof(UWE.Utils), "get_lockCursor")]
        private static class CursorPatch
        {
            private static void Postfix(ref bool __result) { if (Instance != null && Instance.MenuOpen) __result = false; }
        }
        [HarmonyPatch(typeof(UWE.Utils), "UpdateCusorLockState")]
        private static class CursorUpdatePatch
        {
            private static bool Prefix()
            {
                if (Instance == null || !Instance.MenuOpen) return true;
                Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
                return false;
            }
        }
        [HarmonyPatch(typeof(IngameMenu), "Open")]
        private static class PausePatch
        {
            private static void Prefix() { if (Instance != null) Instance.SetMenu(false); }
        }
        [HarmonyPatch(typeof(SaveLoadManager), "SaveWorldStateToTemporaryStorageAsync")]
        private static class SavePatch
        {
            private static void Prefix() { if (Instance != null) { Instance.ResetForm(); Instance.SetMenu(false); } }
        }

        [HarmonyPatch(typeof(GameInput), "GetButtonDown")]
        private static class WheelSlotPatch
        {
            private static void Postfix(GameInput.Button __0, ref bool __result)
            {
                if (Instance != null && Instance.Morphed && !Instance.MenuOpen &&
                    (__0 == GameInput.Button.CycleNext || __0 == GameInput.Button.CyclePrev)) __result = false;
            }
        }
        [HarmonyPatch(typeof(GameInput), "GetMoveDirection")]
        private static class MovePatch
        {
            private static void Postfix(ref Vector3 __result) { if (Instance != null && Instance.MenuOpen) __result = Vector3.zero; }
        }
        [HarmonyPatch(typeof(GameInput), "GetLookDelta")]
        private static class LookPatch
        {
            private static void Postfix(ref Vector2 __result) { if (Instance != null && Instance.MenuOpen) __result = Vector2.zero; }
        }
        [HarmonyPatch(typeof(GUIHand), "OnUpdate")]
        private static class HandPatch
        {
            private static bool Prefix() { return Instance == null || (!Instance.Morphed && !Instance.MenuOpen); }
        }
        [HarmonyPatch(typeof(Player), "CanBreathe")]
        private static class BreathPatch
        {
            private static void Postfix(Player __instance, ref bool __result)
            { if (Instance != null && Instance.Morphed && Instance.owner == __instance && Instance.unlimitedOxygen.Value) __result = true; }
        }
    }
}
