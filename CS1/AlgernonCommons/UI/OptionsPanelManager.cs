// <copyright file="OptionsPanelManager.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace AlgernonCommons.UI
{
    using ColossalFramework.UI;
    using ICities;
    using UnityEngine;

    public static class OptionsPanelManager<TPanel>
        where TPanel : OptionsPanelBase
    {
        private const float PanelMargin = 10f;

        public static void Setup(UIHelperBase helper)
        {
            UIComponent parent = (UIComponent)((UIHelper)helper).self;
            foreach (UIComponent child in parent.components)
            {
                if (child is TPanel)
                {
                    return;
                }
            }

            if (parent is UIScrollablePanel scroll)
            {
                scroll.autoLayout = false;
            }

            TPanel panel = parent.AddUIComponent<TPanel>();
            try
            {
                panel.width = parent.width - (PanelMargin * 2f);
                panel.relativePosition = new Vector2(PanelMargin, PanelMargin);
                panel.Setup();
            }
            catch
            {
                parent.RemoveUIComponent(panel);
                Object.Destroy(panel.gameObject);
                throw;
            }
        }
    }
}
