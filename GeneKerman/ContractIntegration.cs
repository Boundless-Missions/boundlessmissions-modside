/*
 * ContractIntegration.cs – Stock KSP contract system bridge.
 *
 * Injects Gene Kerman missions as stock contracts so they appear in
 * the Mission Control building UI alongside any other contracts.
 *
 * Uses KSP's Contract and ContractParameter classes:
 *   - GKMissionContract: the main contract wrapper
 *   - GKMissionParameter: tracks a single objective
 *
 * Completion is driven by our mod's API status, not stock contract logic.
 * When the API marks a contract as completed, we complete the stock contract too.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Contracts;
using UnityEngine;

namespace GeneKerman
{
    /// <summary>
    /// ScenarioModule that manages the bridge between our API contracts
    /// and the stock contract system. Registered via a MODULE Manager config.
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames,
        GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    public class GKContractScenario : ScenarioModule
    {
        public static GKContractScenario Instance { get; private set; }

        // Maps API contract_id → stock contract guid
        private Dictionary<string, string> activeContracts = new Dictionary<string, string>();
        
        // List of contract_ids whose vessels have already been imported into this save
        private HashSet<string> importedVessels = new HashSet<string>();

        public override void OnAwake()
        {
            Instance = this;
        }

        public override void OnLoad(ConfigNode node)
        {
            // Never let an exception escape into KSP's ScenarioRunner.AddModule — a throw
            // here logs "Exception loading ScenarioModule GKContractScenario" and drops
            // our persisted state. Guard the node and collections defensively.
            if (activeContracts == null) activeContracts = new Dictionary<string, string>();
            if (importedVessels == null) importedVessels = new HashSet<string>();
            activeContracts.Clear();
            importedVessels.Clear();

            if (node == null) return;

            try
            {
                var mappings = node.GetNode("CONTRACT_MAPPINGS");
                if (mappings != null)
                {
                    foreach (ConfigNode.Value val in mappings.values)
                        activeContracts[val.name] = val.value;
                }

                var imports = node.GetNode("IMPORTED_VESSELS");
                if (imports != null)
                {
                    foreach (ConfigNode.Value val in imports.values)
                        importedVessels.Add(val.value);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GKContractScenario.OnLoad failed: {ex.Message}");
            }
        }

        public override void OnSave(ConfigNode node)
        {
            if (node == null) return;
            try
            {
                var mappings = node.AddNode("CONTRACT_MAPPINGS");
                foreach (var kvp in (activeContracts ?? new Dictionary<string, string>()))
                    mappings.AddValue(kvp.Key, kvp.Value);

                var imports = node.AddNode("IMPORTED_VESSELS");
                foreach (var cid in (importedVessels ?? new HashSet<string>()))
                    imports.AddValue("contract_id", cid);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] GKContractScenario.OnSave failed: {ex.Message}");
            }
        }

        public bool HasImportedVessel(string contractId)
        {
            return importedVessels.Contains(contractId);
        }

        public void MarkVesselImported(string contractId)
        {
            importedVessels.Add(contractId);
        }

        /// <summary>
        /// Inject a mission from our API as a stock contract.
        /// </summary>
        public void InjectContract(string apiContractId, string missionDesc,
            int payment, int difficulty, string dueDate)
        {
            if (activeContracts.ContainsKey(apiContractId))
            {
                Debug.Log($"[GeneKerman] Contract {apiContractId} already injected.");
                return;
            }

            try
            {
                // Track the mapping — stock contract injection happens when
                // the ContractSystem is ready and processes our contract type.
                // For now, store the mapping so we can complete/cancel later.
                activeContracts[apiContractId] = apiContractId; // self-mapping until stock contract is created

                Debug.Log($"[GeneKerman] Tracked contract mapping: {apiContractId} → {missionDesc}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[GeneKerman] Failed to track contract: {ex.Message}");
            }
        }

        /// <summary>
        /// Mark a stock contract as completed when our API confirms it.
        /// </summary>
        public void CompleteContract(string apiContractId)
        {
            if (!activeContracts.TryGetValue(apiContractId, out string guid))
                return;

            var contract = ContractSystem.Instance?.Contracts
                .FirstOrDefault(c => c.ContractGuid.ToString() == guid);

            if (contract != null && contract.ContractState == Contract.State.Active)
            {
                // Complete all parameters first
                foreach (var param in contract.AllParameters)
                {
                    if (param is GKMissionParameter gkParam)
                        gkParam.MarkComplete();
                }

                Debug.Log($"[GeneKerman] Stock contract completed: {apiContractId}");
            }

            activeContracts.Remove(apiContractId);
        }

        /// <summary>
        /// Cancel a stock contract when the API contract is cancelled/expired.
        /// </summary>
        public void CancelContract(string apiContractId)
        {
            if (!activeContracts.TryGetValue(apiContractId, out string guid))
                return;

            var contract = ContractSystem.Instance?.Contracts
                .FirstOrDefault(c => c.ContractGuid.ToString() == guid);

            if (contract != null)
            {
                contract.Cancel();
                Debug.Log($"[GeneKerman] Stock contract cancelled: {apiContractId}");
            }

            activeContracts.Remove(apiContractId);
        }
    }

    /// <summary>
    /// Custom contract type for Gene Kerman missions.
    /// Appears in Mission Control with our custom description and rewards.
    /// </summary>
    public class GKMissionContract : Contract
    {
        // Stored data
        private string apiContractId = "";
        private string missionDescription = "";
        private int missionPayment;
        private int missionDifficulty;
        private string missionDueDate = "";

        public void SetMissionData(string contractId, string desc, int payment, int difficulty, string dueDate)
        {
            apiContractId = contractId;
            missionDescription = desc;
            missionPayment = payment;
            missionDifficulty = difficulty;
            missionDueDate = dueDate;
        }

        protected override bool Generate()
        {
            // This is called by the contract system — we handle generation ourselves
            // via InjectContract, so this just needs to return true for manual injection
            SetExpiry();
            SetDeadlineYears(0.1f); // Short deadline
            SetReputation(missionDifficulty * 5f, missionDifficulty * -2f, null);
            SetFunds(0, missionPayment, missionPayment * 0.5f, null);

            // Add a single parameter
            AddParameter(new GKMissionParameter(missionDescription, apiContractId));

            return true;
        }

        public override bool CanBeCancelled() => true;
        public override bool CanBeDeclined() => true;

        protected override string GetTitle()
        {
            return $"[BM] {missionDescription}";
        }

        protected override string GetDescription()
        {
            return $"Boundless Missions assignment.\n\n" +
                   $"Mission: {missionDescription}\n" +
                   $"Difficulty: {missionDifficulty}/10\n" +
                   $"Due: {missionDueDate}\n\n" +
                   $"Submit your completion from the Boundless Missions mod panel (Boundless Missions toolbar button).";
        }

        protected override string GetSynopsys()
        {
            return missionDescription;
        }

        protected override string MessageCompleted()
        {
            return $"Mission completed! Rewards distributed via Boundless Missions system.";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("gk_contractId", apiContractId);
            node.AddValue("gk_description", missionDescription);
            node.AddValue("gk_payment", missionPayment);
            node.AddValue("gk_difficulty", missionDifficulty);
            node.AddValue("gk_dueDate", missionDueDate);
        }

        protected override void OnLoad(ConfigNode node)
        {
            apiContractId = node.GetValue("gk_contractId") ?? "";
            missionDescription = node.GetValue("gk_description") ?? "";
            int.TryParse(node.GetValue("gk_payment") ?? "0", out missionPayment);
            int.TryParse(node.GetValue("gk_difficulty") ?? "0", out missionDifficulty);
            missionDueDate = node.GetValue("gk_dueDate") ?? "";
        }

        public override bool MeetRequirements() => true;
    }

    /// <summary>
    /// Parameter that tracks a Gene Kerman mission objective.
    /// Completion is driven by the API, not by in-game events.
    /// </summary>
    public class GKMissionParameter : ContractParameter
    {
        private string description = "";
        private string apiContractId = "";

        public GKMissionParameter() { } // Required for deserialization

        public GKMissionParameter(string desc, string contractId)
        {
            description = desc;
            apiContractId = contractId;
        }

        protected override string GetTitle()
        {
            return description;
        }

        protected override string GetHashString()
        {
            return $"GKMission_{apiContractId}";
        }

        protected override void OnSave(ConfigNode node)
        {
            node.AddValue("gk_desc", description);
            node.AddValue("gk_cid", apiContractId);
        }

        protected override void OnLoad(ConfigNode node)
        {
            description = node.GetValue("gk_desc") ?? "";
            apiContractId = node.GetValue("gk_cid") ?? "";
        }

        /// <summary>
        /// Called by our scenario when the API confirms completion.
        /// </summary>
        public void MarkComplete()
        {
            SetComplete();
        }
    }
}
