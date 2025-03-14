﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Quantum.Simulation.Common;
using Microsoft.Quantum.Simulation.Core;
using System;
using System.Diagnostics;

namespace Microsoft.Quantum.Simulation.Simulators
{
    public partial class CommonNativeSimulator : SimulatorBase, IDisposable
    {
        class QSimQubit : Qubit
        {
            [DebuggerBrowsable(DebuggerBrowsableState.Never)]
            private static Pauli[] PAULI_Z = new Pauli[] { Pauli.PauliZ };

            private CommonNativeSimulator Simulator { get; }
        
            public QSimQubit(int id, CommonNativeSimulator sim) : base(id)
            {
                this.Simulator = sim;
            }

            public double Probability => this.Simulator.JointEnsembleProbability(1, PAULI_Z, new uint[] { (uint)this.Id });
        }
    }
}
