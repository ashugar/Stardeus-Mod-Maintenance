using ColonistAndBotsAutoUpgrade.Models;
using ColonistAndBotsAutoUpgrade.Services;
using Game.CodeGen;
using Game.Constants;
using Game.Data;
using Game.Systems.UIUX;
using Game.UI;
using Game.Utils;
using KL.Utils;
using System.Collections.Generic;
using UnityEngine;

namespace ColonistAndBotsAutoUpgrade.UI
{
    public sealed class EnemyConversionEntityAction : BaseEntityAction
    {
        public const string ActionId =
            "ColonistAndBotsAutoUpgrade.EnemyConversion";
        public override string Id => ActionId;
        public override string HotkeyLabel => "Convert";

        [SelfInit(SelfInit.Stage.CoreDefs, 0)]
        private static void Register()
        {
            RegisterAction(new EnemyConversionEntityAction());
        }

        protected override bool ApplyFor(
          GameState state,
          Being being,
          out EntityActionData data
        )
        {
            if (!CARAUSettings.AllowEnemyConversion ||
                EnemyConversionService.Evaluate(state, being).IsQueueable != true)
            {
                data = EntityActionData.Unavailable;
                return false;
            }

            data = new EntityActionData(
                this,
                (life, parent, index) =>
                    Create(life, parent, index, state, being),
                Priority.Normal
            );
            return true;
        }

        protected override bool ApplyForMany(
          GameState state,
          List<Being> beings,
          out EntityActionData data
        )
        {
            if (!CARAUSettings.AllowEnemyConversion)
            {
                data = EntityActionData.Unavailable;
                return false;
            }

            var selected = beings == null
                ? new List<Being>()
                : new List<Being>(beings);
            var count = EnemyConversionService.BuildPlans(
                state,
                selected
            ).Count;
            if (count == 0 || !ValidateLimit(count, "Convert All", true))
            {
                data = EntityActionData.Unavailable;
                return false;
            }

            data = new EntityActionData(
                this,
                (life, parent, index) =>
                    CreateMulti(
                        life,
                        parent,
                        index,
                        state,
                        selected,
                        count
                    ),
                Priority.Normal
            );
            return true;
        }

        private WBase Create(
          Lifetime life,
          RectTransform parent,
          int siblingIndex,
          GameState state,
          Being being
        )
        {
            var button = SpawnButton(life, true, parent, siblingIndex);
            button.Load(
                IconId.CRobotEyeGreen,
                "Convert",
                () => EnemyConversionConfirmation.Request(
                    state,
                    new[] { being }
                )
            );
            UIUtils.AddTip(
                life,
                button,
                "Confirm an exact Obey Chip or Core Swap plan for this " +
                "incapacitated hostile Being."
            );
            return button;
        }

        private WBase CreateMulti(
          Lifetime life,
          RectTransform parent,
          int siblingIndex,
          GameState state,
          List<Being> beings,
          int count
        )
        {
            var button = SpawnButtonMulti(
                life,
                true,
                parent,
                siblingIndex
            );
            button.LoadMulti(
                count,
                IconId.CRobotEyeGreen,
                "Convert All",
                () => RequestMulti(state, beings)
            );
            UIUtils.AddTip(
                life,
                button,
                "Confirm exact conversion plans for eligible hostile, " +
                "living, incapacitated Beings in this selection."
            );
            return button;
        }

        private static void RequestMulti(
          GameState state,
          List<Being> beings
        )
        {
            var liveCount = EnemyConversionService.BuildPlans(
                state,
                beings
            ).Count;
            if (liveCount == 0 ||
                !ValidateLimit(liveCount, "Convert All", false))
            {
                return;
            }

            EnemyConversionConfirmation.Request(state, beings);
        }
    }
}
