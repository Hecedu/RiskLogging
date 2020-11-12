﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Risk.Shared
{
    public class ContinueAttackRequest
    {
        public IEnumerable<Territory> Board { get; set; }
        public Territory AttackingTerritorry { get; set; }
        public Territory DefendingTerritorry { get; set; }
    }
}
