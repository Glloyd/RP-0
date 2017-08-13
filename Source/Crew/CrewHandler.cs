﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KSP;
using UnityEngine;
using System.Reflection;

namespace RP0.Crew
{
    [KSPScenario((ScenarioCreationOptions)120, new GameScenes[] { GameScenes.EDITOR, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION })]
    public class CrewHandler : ScenarioModule
    {
        #region TrainingExpiration

        public class TrainingExpiration : IConfigNode
        {
            public string pcmName;

            public List<string> entries = new List<string>();

            public double expiration;

            public TrainingExpiration() { }

            public TrainingExpiration(ConfigNode node)
            {
                Load(node);
            }

            public bool Compare(int idx, FlightLog.Entry e)
            {
                string str = entries[idx];
                int tyLen = (string.IsNullOrEmpty(e.type) ? 0 : e.type.Length);
                int tgLen = (string.IsNullOrEmpty(e.target ) ? 0 : e.target.Length);
                int iC = str.Length;
                if (iC != 1 + tyLen + tgLen)
                    return false;
                int i = 0;
                for (; i < tyLen; ++i)
                {
                    if (str[i] != e.type[i])
                        return false;
                }

                if (str[i] != ',')
                    return false;
                ++i;
                for (int j = 0; j < tgLen && i < iC; ++j)
                {
                    if (str[i] != e.target[j])
                        return false;
                    ++i;
                }

                return true;
            }

            public void Load(ConfigNode node)
            {
                foreach (ConfigNode.Value v in node.values)
                {
                    switch (v.name)
                    {
                        case "pcmName":
                            pcmName = v.value;
                            break;
                        case "expiration":
                            double.TryParse(v.value, out expiration);
                            break;

                        default:
                        case "entry":
                            entries.Add(v.value);
                            break;
                    }
                }
            }

            public void Save(ConfigNode node)
            {
                node.AddValue("pcmName", pcmName);
                node.AddValue("expiration", expiration);
                foreach (string s in entries)
                    node.AddValue("entry", s);
            }
        }

        #endregion

        #region Fields

        public CrewHandlerSettings settings = new CrewHandlerSettings();

        protected Dictionary<string, double> kerbalRetireTimes = new Dictionary<string, double>();

        protected HashSet<string> retirees = new HashSet<string>();

        protected static HashSet<string> toRemove = new HashSet<string>();

        protected List<TrainingExpiration> expireTimes = new List<TrainingExpiration>();

        protected bool inAC = false;

        protected KSP.UI.Screens.AstronautComplex astronautComplex = null;

        protected int countAvailable, countAssigned, countKIA;

        protected bool firstLoad = true;

        protected FieldInfo cliTooltip;

        [KSPField(isPersistant = true)]
        public double nextUpdate = -1d;

        protected double updateInterval = 3600d;



        public List<CourseTemplate> CourseTemplates = new List<CourseTemplate>();
        public List<CourseTemplate> OfferedCourses = new List<CourseTemplate>();
        public List<ActiveCourse> ActiveCourses = new List<ActiveCourse>();
        protected HashSet<string> partSynsHandled = new HashSet<string>();
        protected TrainingDatabase trainingDatabase = new TrainingDatabase();

        public FSGUI fsGUI = new FSGUI();

        #region Instance

        private static CrewHandler _instance = null;
        public static CrewHandler Instance
        {
            get
            {
                return _instance;
            }
        }

        #endregion

        #endregion

        #region Overrides and Monobehaviour methods

        public override void OnAwake()
        {

            if (_instance != null)
            {
                GameObject.Destroy(_instance);
            }
            _instance = this;

            GameEvents.OnVesselRecoveryRequested.Add(VesselRecoveryRequested);
            GameEvents.OnCrewmemberHired.Add(OnCrewHired);
            GameEvents.onGUIAstronautComplexSpawn.Add(ACSpawn);
            GameEvents.onGUIAstronautComplexDespawn.Add(ACDespawn);
            GameEvents.OnPartPurchased.Add(new EventData<AvailablePart>.OnEvent(onPartPurchased));

            cliTooltip = typeof(KSP.UI.CrewListItem).GetField("tooltipController", BindingFlags.NonPublic | BindingFlags.Instance);

            FindAllCourseConfigs(); //find all applicable configs
            GenerateOfferedCourses(); //turn the configs into offered courses
        }

        public override void OnLoad(ConfigNode node)
        {
            base.OnLoad(node);

            foreach (ConfigNode stg in GameDatabase.Instance.GetConfigNodes("CREWHANDLERSETTINGS"))
                settings.Load(stg);

            kerbalRetireTimes.Clear();
            ConfigNode n = node.GetNode("RETIRETIMES");
            if (n != null)
            {
                foreach (ConfigNode.Value v in n.values)
                    kerbalRetireTimes[v.name] = double.Parse(v.value);
            }

            retirees.Clear();
            n = node.GetNode("RETIREES");
            if (n != null)
            {
                foreach (ConfigNode.Value v in n.values)
                    retirees.Add(v.value);
            }

            expireTimes.Clear();
            n = node.GetNode("EXPIRATIONS");
            if (n != null)
            {
                foreach (ConfigNode eN in n.nodes)
                {
                    expireTimes.Add(new TrainingExpiration(eN));
                }
            }

            ConfigNode FSData = node.GetNode("FlightSchoolData");

            if (FSData == null)
                return;

            //load all the active courses
            ActiveCourses.Clear();
            foreach (ConfigNode courseNode in FSData.GetNodes("ACTIVE_COURSE"))
            {
                try
                {
                    ActiveCourses.Add(new ActiveCourse(courseNode));
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            TrainingDatabase.Initialize();
        }

        public override void OnSave(ConfigNode node)
        {
            base.OnSave(node);

            ConfigNode n = node.AddNode("RETIRETIMES");
            foreach (KeyValuePair<string, double> kvp in kerbalRetireTimes)
                n.AddValue(kvp.Key, kvp.Value);

            n = node.AddNode("RETIREES");
            foreach (string s in retirees)
                n.AddValue("retiree", s);

            n = node.AddNode("EXPIRATIONS");
            foreach (TrainingExpiration e in expireTimes)
                e.Save(n.AddNode("Expiration"));

            ConfigNode FSData = new ConfigNode("FlightSchoolData");
            //save all the active courses
            foreach (ActiveCourse course in ActiveCourses)
            {
                ConfigNode courseNode = course.AsConfigNode();
                FSData.AddNode("ACTIVE_COURSE", courseNode);
            }
            node.AddNode("FlightSchoolData", FSData);
        }

        public void Update()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.CrewRoster == null)
                return;

            // Catch earlies
            if (firstLoad)
            {
                firstLoad = false;
                List<string> newHires = new List<string>();

                foreach (ProtoCrewMember pcm in HighLogic.CurrentGame.CrewRoster.Crew)
                {
                    if ((pcm.rosterStatus == ProtoCrewMember.RosterStatus.Assigned || pcm.rosterStatus == ProtoCrewMember.RosterStatus.Available) && !kerbalRetireTimes.ContainsKey(pcm.name))
                    {
                        newHires.Add(pcm.name);
                        OnCrewHired(pcm, int.MinValue);
                    }
                }
                if (newHires.Count > 0)
                {
                    string msgStr = "Crew will retire as follows:";
                    foreach (string s in newHires)
                        msgStr += "\n" + s + ", no earlier than " + KSPUtil.PrintDate(kerbalRetireTimes[s], false);

                    PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                                                        new Vector2(0.5f, 0.5f),
                                                        "Initial Retirement Date",
                                                        msgStr
                                                        + "\n(Retirement will be delayed the more intersting flights they fly.)",
                                                        "OK",
                                                        false,
                                                        HighLogic.UISkin);
                }
            }

            // Retirements
            double time = Planetarium.GetUniversalTime();
            if (nextUpdate < time)
            {
                nextUpdate = time + updateInterval;

                foreach (KeyValuePair<string, double> kvp in kerbalRetireTimes)
                {
                    ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[kvp.Key];
                    if (pcm == null)
                        toRemove.Add(kvp.Key);
                    else
                    {
                        if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Available)
                        {
                            if (pcm.rosterStatus != ProtoCrewMember.RosterStatus.Assigned)
                                toRemove.Add(kvp.Key);

                            continue;
                        }

                        if (pcm.inactive)
                            continue;

                        if (time > kvp.Value)
                        {
                            toRemove.Add(kvp.Key);
                            retirees.Add(kvp.Key);
                            pcm.rosterStatus = ProtoCrewMember.RosterStatus.Dead;
                        }
                    }
                }

                for (int i = ActiveCourses.Count; i-- > 0;)
                {
                    ActiveCourse course = ActiveCourses[i];
                    if (course.ProgressTime(time)) //returns true when the course completes
                    {
                        ActiveCourses.RemoveAt(i);
                    }
                }

                for (int i = expireTimes.Count; i-- > 0;)
                {
                    TrainingExpiration e = expireTimes[i];
                    if (time > e.expiration)
                    {
                        ProtoCrewMember pcm = HighLogic.CurrentGame.CrewRoster[e.pcmName];
                        if (pcm != null)
                        {
                            for (int j = pcm.flightLog.Entries.Count; j-- > 0;)
                            {
                                int eC = e.entries.Count;
                                if (eC == 0)
                                    break;
                                FlightLog.Entry ent = pcm.flightLog[j];
                                for (int k = eC; k-- > 0;)
                                {
                                    if (e.Compare(k, ent))
                                    {
                                        ScreenMessages.PostScreenMessage(pcm.name + ": Expired: " + GetPrettyCourseName(ent.type) + ent.target);
                                        ent.type = "expired_" + ent.type;
                                        e.entries.RemoveAt(k);
                                    }
                                }
                            }
                        }
                        expireTimes.RemoveAt(i);
                    }
                }

                // TODO remove from courses? Except I think they won't retire if inactive either so that's ok.
                if (toRemove.Count > 0)
                {
                    string msgStr = string.Empty;
                    foreach (string s in toRemove)
                    {
                        kerbalRetireTimes.Remove(s);
                        if (HighLogic.CurrentGame.CrewRoster[s] != null)
                            msgStr += "\n" + s;
                    }
                    if (!string.IsNullOrEmpty(msgStr))
                    {

                        PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                                                            new Vector2(0.5f, 0.5f),
                                                            "Crew Retirement",
                                                            "The following retirements have occurred:\n" + msgStr,
                                                            "OK",
                                                            true,
                                                            HighLogic.UISkin);
                    }

                    toRemove.Clear();
                }
            }

            // UI fixing
            if (inAC)
            {
                if (astronautComplex == null)
                {
                    KSP.UI.Screens.AstronautComplex[] mbs = GameObject.FindObjectsOfType<KSP.UI.Screens.AstronautComplex>();
                    int maxCount = -1;
                    foreach (KSP.UI.Screens.AstronautComplex c in mbs)
                    {
                        int count = c.ScrollListApplicants.Count + c.ScrollListAssigned.Count + c.ScrollListAvailable.Count + c.ScrollListKia.Count;
                        if (count > maxCount)
                        {
                            maxCount = count;
                            astronautComplex = c;
                        }
                    }

                    if (astronautComplex == null)
                        return;
                }
                int newAv = astronautComplex.ScrollListAvailable.Count;
                int newAsgn = astronautComplex.ScrollListAssigned.Count;
                int newKIA = astronautComplex.ScrollListKia.Count;
                if (newAv != countAvailable || newKIA != countKIA || newAsgn != countAssigned)
                {
                    countAvailable = newAv;
                    countAssigned = newAsgn;
                    countKIA = newKIA;

                    foreach (KSP.UI.UIListData<KSP.UI.UIListItem> u in astronautComplex.ScrollListAvailable)
                    {
                        KSP.UI.CrewListItem cli = u.listItem.GetComponent<KSP.UI.CrewListItem>();
                        if (cli != null)
                        {
                            FixTooltip(cli);
                            if (cli.GetCrewRef().inactive)
                            {
                                cli.MouseoverEnabled = false;
                                cli.SetLabel("Recovering");
                            }
                        }
                    }

                    foreach (KSP.UI.UIListData<KSP.UI.UIListItem> u in astronautComplex.ScrollListAssigned)
                    {
                        KSP.UI.CrewListItem cli = u.listItem.GetComponent<KSP.UI.CrewListItem>();
                        if (cli != null)
                        {
                            FixTooltip(cli);
                        }
                    }

                    foreach (KSP.UI.UIListData<KSP.UI.UIListItem> u in astronautComplex.ScrollListKia)
                    {
                        KSP.UI.CrewListItem cli = u.listItem.GetComponent<KSP.UI.CrewListItem>();
                        if (cli != null)
                        {
                            if (retirees.Contains(cli.GetName()))
                            {
                                cli.SetLabel("Retired");
                                cli.MouseoverEnabled = false;
                            }
                        }
                    }
                }
            }
        }

        public void OnDestroy()
        {
            GameEvents.OnVesselRecoveryRequested.Remove(VesselRecoveryRequested);
            GameEvents.OnCrewmemberHired.Remove(OnCrewHired);
            GameEvents.onGUIAstronautComplexSpawn.Remove(ACSpawn);
            GameEvents.onGUIAstronautComplexDespawn.Remove(ACDespawn);
            GameEvents.OnPartPurchased.Remove(new EventData<AvailablePart>.OnEvent(onPartPurchased));
        }

        #endregion

        #region Interfaces

        public void AddExpiration(TrainingExpiration e)
        {
            expireTimes.Add(e);
        }

        #endregion

        #region Methods

        protected void ACSpawn()
        {
            inAC = true;
            countAvailable = countKIA = -1;
        }

        protected void ACDespawn()
        {
            inAC = false;
            astronautComplex = null;
        }

        protected void VesselRecoveryRequested(Vessel v)
        {
            double elapsedTime = v.missionTime;
            List<string> retirementChanges = new List<string>();
            List<string> inactivity = new List<string>();

            double UT = Planetarium.GetUniversalTime();

            foreach (ProtoCrewMember pcm in v.GetVesselCrew())
            {
                bool hasSpace = false;
                bool hasOrbit = false;
                bool hasEVA = false;
                bool hasEVAOther = false;
                bool hasOther = false;
                bool hasOrbitOther = false;
                bool hasLandOther = false;
                int curFlight = pcm.flightLog.Last().flight;
                foreach (FlightLog.Entry e in pcm.flightLog.Entries)
                {
                    if (e.flight != curFlight)
                        continue;

                    bool isOther = false;
                    if (!string.IsNullOrEmpty(e.target) && e.target != Planetarium.fetch.Home.name)
                        isOther = hasOther = true;

                    if (!string.IsNullOrEmpty(e.type))
                    {
                        switch (e.type)
                        {
                            case "Suborbit":
                                hasSpace = true;
                                break;
                            case "Orbit":
                                if (isOther)
                                    hasOrbitOther = true;
                                else
                                    hasOrbit = true;
                                break;
                            case "ExitVessel":
                                if (isOther)
                                    hasEVAOther = true;
                                else
                                    hasEVA = true;
                                break;
                            case "Land":
                                if (isOther)
                                    hasLandOther = true;
                                break;
                            default:
                                break;
                        }
                    }
                }
                double multiplier = 1d;
                double constant = 0.5d;
                if (hasSpace)
                {
                    multiplier += settings.recSpace.x;
                    constant += settings.recSpace.y;
                }
                if (hasOrbit)
                {
                    multiplier += settings.recOrbit.x;
                    constant += settings.recOrbit.y;
                }
                if (hasOther)
                {
                    multiplier += settings.recOtherBody.x;
                    constant += settings.recOtherBody.y;
                }
                if (hasEVA)
                {
                    multiplier += settings.recEVA.x;
                    constant += settings.recEVA.y;
                }
                if (hasEVAOther)
                {
                    multiplier += settings.recEVAOther.x;
                    constant += settings.recEVAOther.y;
                }
                if (hasOrbitOther)
                {
                    multiplier += settings.recOrbitOther.x;
                    constant += settings.recOrbitOther.y;
                }
                if (hasLandOther)
                {
                    multiplier += settings.recLandOther.x;
                    constant += settings.recLandOther.y;
                }

                double retTime;
                if (kerbalRetireTimes.TryGetValue(pcm.name, out retTime))
                {
                    double offset = constant * 86400d * settings.retireOffsetBaseMult / (1 + Math.Pow(Math.Max(curFlight + settings.retireOffsetFlightNumOffset, 0d), settings.retireOffsetFlightNumPow)
                        * UtilMath.Lerp(settings.retireOffsetStupidMin, settings.retireOffsetStupidMax, pcm.stupidity));
                    if (offset > 0d)
                    {
                        retTime += offset;
                        kerbalRetireTimes[pcm.name] = retTime;
                        retirementChanges.Add("\n" + pcm.name + ", no earlier than " + KSPUtil.PrintDate(retTime, false));
                    }
                }

                multiplier /= (ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.AstronautComplex) + 1d);

                double inactiveTime = elapsedTime * multiplier + constant * 86400d;
                pcm.SetInactive(inactiveTime, false);
                inactivity.Add("\n" + pcm.name + ", until " + KSPUtil.PrintDate(inactiveTime + UT, true, false));
            }
            if (inactivity.Count > 0)
            {
                string msgStr = "The following crew members will be on leave:";
                foreach (string s in inactivity)
                {
                    msgStr += s;
                }


                if (retirementChanges.Count > 0)
                {
                    msgStr += "\n\nThe following retirement changes have occurred:";
                    foreach (string s in retirementChanges)
                        msgStr += s;
                }

                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                                                        new Vector2(0.5f, 0.5f),
                                                        "Crew Updates",
                                                        msgStr,
                                                        "OK",
                                                        true,
                                                        HighLogic.UISkin);
            }
        }

        protected void OnCrewHired(ProtoCrewMember pcm, int idx)
        {
            double retireTime = Planetarium.GetUniversalTime() + GetServiceTime(pcm);
            kerbalRetireTimes[pcm.name] = retireTime;
            if (idx != int.MinValue)
            {
                PopupDialog.SpawnPopupDialog(new Vector2(0.5f, 0.5f),
                                                        new Vector2(0.5f, 0.5f),
                                                        "Initial Retirement Date",
                                                        pcm.name + " will retire no earlier than " + KSPUtil.PrintDate(retireTime, false)
                                                        + "\n(Retirement will be delayed the more intersting flights they fly.)",
                                                        "OK",
                                                        false,
                                                        HighLogic.UISkin);
            }

        }

        protected double GetServiceTime(ProtoCrewMember pcm)
        {
            return 86400d * 365d * (settings.retireBaseYears
                + UtilMath.Lerp(settings.retireCourageMin, settings.retireCourageMax, pcm.courage)
                + UtilMath.Lerp(settings.retireStupidMin, settings.retireStupidMax, pcm.stupidity));
        }

        protected void FixTooltip(KSP.UI.CrewListItem cli)
        {
            ProtoCrewMember pcm = cli.GetCrewRef();
            double retTime;
            if (kerbalRetireTimes.TryGetValue(pcm.name, out retTime))
            {
                cli.SetTooltip(pcm);
                KSP.UI.TooltipTypes.TooltipController_CrewAC ttc = cliTooltip.GetValue(cli) as KSP.UI.TooltipTypes.TooltipController_CrewAC;
                ttc.descriptionString += "\n\nRetires no earlier than " + KSPUtil.PrintDate(retTime, false);

                // Training

                string trainingStr = GetTrainingString(pcm);
                if (!string.IsNullOrEmpty(trainingStr))
                    ttc.descriptionString += trainingStr;
            }
        }

        protected string GetTrainingString(ProtoCrewMember pcm)
        {
            HashSet<string> expiredProfs = new HashSet<string>();
            bool found = false;
            string trainingStr = "\n\nTraining:";
            int lastFlight = pcm.flightLog.Last() == null ? 0 : pcm.flightLog.Last().flight;
            foreach (FlightLog.Entry ent in pcm.flightLog.Entries)
            {
                string pretty = GetPrettyCourseName(ent.type);
                if (!string.IsNullOrEmpty(pretty))
                {
                    if (ent.type == "expired_TRAINING_proficiency")
                    {
                        found = true;
                        expiredProfs.Add(ent.target);
                    }
                    else
                    {
                        if (ent.type == "TRAINING_mission" && ent.flight != lastFlight)
                            continue;

                        found = true;
                        trainingStr += "\n  " + pretty + ent.target;
                        double exp = GetExpiration(pcm.name, ent);
                        if (exp > 0d)
                            trainingStr += ". Expires " + KSPUtil.PrintDate(exp, false);
                    }
                }
            }
            if (expiredProfs.Count > 0)
                trainingStr += "\n  Expired proficiencies:";
            foreach (string s in expiredProfs)
                trainingStr += "\n    " + s;

            if (found)
                return trainingStr;
            else
                return string.Empty;
        }

        protected double GetExpiration(string pcmName, FlightLog.Entry ent)
        {
            for (int i = expireTimes.Count; i-- > 0;)
            {
                TrainingExpiration e = expireTimes[i];
                if (e.pcmName == pcmName)
                {
                    for (int j = e.entries.Count; j-- > 0;)
                    {
                        if (e.Compare(j, ent))
                            return e.expiration;
                    }
                }
            }

            return 0d;
        }

        /* UI: display list of retirement NET dates.  Called from MaintenanceWindow */
        public void nautList()
        {
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Space(40);
                GUILayout.Label("Name", HighLogic.Skin.label, GUILayout.Width(120));
                GUILayout.Label("Retires NET", HighLogic.Skin.label, GUILayout.Width(80));
            } finally {
                GUILayout.EndHorizontal();
            }
            foreach (string name in kerbalRetireTimes.Keys) {
                GUILayout.BeginHorizontal();
                try {
                    GUILayout.Space(40);
                    double rt = kerbalRetireTimes[name];
                    GUILayout.Label(name, HighLogic.Skin.label, GUILayout.Width(120));
                    GUILayout.Label(KSPUtil.PrintDate(rt, false), HighLogic.Skin.label, GUILayout.Width(80));
                } finally {
                    GUILayout.EndHorizontal();
                }
            }
        }

        protected void FindAllCourseConfigs()
        {
            CourseTemplates.Clear();
            //find all configs and save them
            foreach (ConfigNode course in GameDatabase.Instance.GetConfigNodes("FS_COURSE"))
            {
                CourseTemplates.Add(new CourseTemplate(course));
            }
            Debug.Log("[FS] Found " + CourseTemplates.Count + " courses.");
            //fire an event to let other mods add their configs
        }
        
        protected void GenerateOfferedCourses() //somehow provide some variable options here?
        {
            //convert the saved configs to course offerings
            foreach (CourseTemplate template in CourseTemplates)
            {
                CourseTemplate duplicate = new CourseTemplate(template.sourceNode, true); //creates a duplicate so the initial template is preserved
                duplicate.PopulateFromSourceNode();
                if (duplicate.Available)
                    OfferedCourses.Add(duplicate);
            }

            foreach (AvailablePart ap in PartLoader.LoadedPartsList)
            {
                if (ap.partPrefab.CrewCapacity > 0 /*&& ap.TechRequired != "start"*/)
                {
                    if (ResearchAndDevelopment.PartModelPurchased(ap))
                    {
                        string name = TrainingDatabase.SynonymReplace(ap.name);
                        if (!partSynsHandled.Contains(ap.name))
                        {
                            partSynsHandled.Add(name);
                            AddPartCourses(ap);
                        }
                    }
                }
            }

            Debug.Log("[FS] Offering " + OfferedCourses.Count + " courses.");
            //fire an event to let other mods add available courses (where they can pass variables through then)
        }

        protected void AddPartCourses(AvailablePart ap)
        {
            GenerateCourseProf(ap);
            GenerateCourseMission(ap);
        }

        protected void GenerateCourseProf(AvailablePart ap)
        {
            ConfigNode n = new ConfigNode("FS_COURSE");
            string name = TrainingDatabase.SynonymReplace(ap.name);

            n.AddValue("id", "prof_" + name);
            n.AddValue("name", "Proficiency: " + name);
            n.AddValue("time", 1d + (TrainingDatabase.GetTime(name) * 86400d));
            n.AddValue("expiration", settings.trainingProficiencyExpirationYears * 86400d * 365d);
            n.AddValue("expirationUseStupid", true);

            n.AddValue("conflicts", "TRAINING_proficiency:" + name);

            ConfigNode r = n.AddNode("REWARD");
            r.AddValue("XPAmt", settings.trainingProficiencyXP);
            ConfigNode l = r.AddNode("FLIGHTLOG");
            l.AddValue("0", "TRAINING_proficiency," + name);

            CourseTemplate c = new CourseTemplate(n);
            c.PopulateFromSourceNode();
            OfferedCourses.Add(c);

            ConfigNode n2 = n.CreateCopy();
            n2.SetValue("id", "profR_" + name);
            n2.SetValue("name", "Refresher: " + name);
            n2.SetValue("time", 1d + TrainingDatabase.GetTime(name) * 86400d * settings.trainingProficiencyRefresherTimeMult);
            n2.AddValue("preReqs", "expired_TRAINING_proficiency:" + name);
            r = n2.GetNode("REWARD");
            r.SetValue("XPAmt", "0");

            c = new CourseTemplate(n2);
            c.PopulateFromSourceNode();
            OfferedCourses.Add(c);
        }

        protected void GenerateCourseMission(AvailablePart ap)
        {
            ConfigNode n = new ConfigNode("FS_COURSE");
            string name = TrainingDatabase.SynonymReplace(ap.name);

            n.AddValue("id", "msn_" + name);
            n.AddValue("name", "Mission: " + name);
            n.AddValue("time", 1d + TrainingDatabase.GetTime(name + "-Mission") * 86400d);
            n.AddValue("timeUseStupid", true);
            n.AddValue("seatMax", ap.partPrefab.CrewCapacity * 2);
            n.AddValue("expiration", settings.trainingMissionExpirationDays * 86400d);

            n.AddValue("preReqs", "TRAINING_proficiency:" + name);
            n.AddValue("conflicts", "TRAINING_mission:" + name);

            ConfigNode r = n.AddNode("REWARD");
            ConfigNode l = r.AddNode("FLIGHTLOG");
            l.AddValue("0", "TRAINING_mission," + name);

            CourseTemplate c = new CourseTemplate(n);
            c.PopulateFromSourceNode();
            OfferedCourses.Add(c);
        }

        protected void onPartPurchased(AvailablePart ap)
        {
            AddPartCourses(ap);
        }

        protected string GetPrettyCourseName(string str)
        {
            switch (str)
            {
                case "TRAINING_proficiency":
                    return "Proficiency with ";
                case "expired_TRAINING_proficiency":
                    return "(Expired) Proficiency with ";
                case "TRAINING_mission":
                    return "Mission training for ";
                /*case "expired_TRAINING_mission":
                    return "(Expired) Mission training for ";*/
                default:
                    return string.Empty;
            }
        }

        #endregion
    }
}
