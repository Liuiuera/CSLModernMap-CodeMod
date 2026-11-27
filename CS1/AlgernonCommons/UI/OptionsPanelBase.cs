// <copyright file="OptionsPanelBase.cs" company="algernon (K. Algernon A. Sheppard)">
// Copyright (c) algernon (K. Algernon A. Sheppard). All rights reserved.
// Licensed under the MIT license. See LICENSE.txt file in the project root for full license information.
// </copyright>

namespace AlgernonCommons.UI
{
    using ColossalFramework.UI;

    /// <summary>
    /// Mod options panel base class.
    /// </summary>
    public abstract class OptionsPanelBase : UIPanel
    {
        internal abstract void Setup();
    }
}
