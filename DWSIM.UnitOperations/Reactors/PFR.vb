'    PFR Calculation Routines 
'    Copyright 2008 Daniel Wagner O. de Medeiros
'
'    This file is part of DWSIM.
'
'    DWSIM is free software: you can redistribute it and/or modify
'    it under the terms of the GNU General Public License as published by
'    the Free Software Foundation, either version 3 of the License, or
'    (at your option) any later version.
'
'    DWSIM is distributed in the hope that it will be useful,
'    but WITHOUT ANY WARRANTY; without even the implied warranty of
'    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
'    GNU General Public License for more details.
'
'    You should have received a copy of the GNU General Public License
'    along with DWSIM.  If not, see <http://www.gnu.org/licenses/>.

Imports DWSIM.DrawingTools.GraphicObjects
Imports DWSIM.Thermodynamics.BaseClasses
Imports Ciloci.Flee
Imports System.Math
Imports System.Linq
Imports DWSIM.Interfaces.Enums
Imports DWSIM.SharedClasses
Imports DWSIM.Thermodynamics.Streams
Imports DWSIM.Thermodynamics
Imports DWSIM.MathOps

Namespace Reactors

    <System.Serializable()> Public Class Reactor_PFR

        Inherits Reactor

        Protected m_vol As Double
        Protected m_dv As Double = 0.05

        Dim C0 As Dictionary(Of String, Double)
        Dim C As Dictionary(Of String, Double)
        Dim Ri As Dictionary(Of String, Double)
        Dim Kf, Kr As ArrayList
        Dim DN As Dictionary(Of String, Double)
        Dim N00 As Dictionary(Of String, Double)
        Dim Rxi As New Dictionary(Of String, Double)
        Dim DHRi As New Dictionary(Of String, Double)

        Public points As ArrayList

        Dim activeAL As Integer = 0

        <System.NonSerialized()> Dim ims As MaterialStream
        <System.NonSerialized()> Dim pp As PropertyPackages.PropertyPackage
        <System.NonSerialized()> Dim ppr As New PropertyPackages.RaoultPropertyPackage()

        Public Property Length As Double = 1.0#

        Public Property Volume() As Double
            Get
                Return m_vol
            End Get
            Set(ByVal value As Double)
                m_vol = value
            End Set
        End Property

        Public Property dV() As Double
            Get
                Return m_dv
            End Get
            Set(ByVal value As Double)
                m_dv = value
            End Set
        End Property

        Public Property CatalystLoading As Double = 0.0#

        Public Property CatalystVoidFraction As Double = 0.0#

        Public Property CatalystParticleDiameter As Double = 0.0#

        Public Sub New()

            MyBase.New()

            '
            '

            'N00 = New Dictionary(Of String, Double)
            'DN = New Dictionary(Of String, Double)
            'C0 = New Dictionary(Of String, Double)
            'C = New Dictionary(Of String, Double)
            'Ri = New Dictionary(Of String, Double)
            'DHRi = New Dictionary(Of String, Double)
            'Kf = New ArrayList
            'Kr = New ArrayList
            'Rxi = New Dictionary(Of String, Double)

        End Sub

        Public Sub New(ByVal name As String, ByVal description As String)

            MyBase.New()
            Me.ComponentName = name
            Me.ComponentDescription = description



            N00 = New Dictionary(Of String, Double)
            DN = New Dictionary(Of String, Double)
            C0 = New Dictionary(Of String, Double)
            C = New Dictionary(Of String, Double)
            Ri = New Dictionary(Of String, Double)
            DHRi = New Dictionary(Of String, Double)
            Kf = New ArrayList
            Kr = New ArrayList
            Rxi = New Dictionary(Of String, Double)

        End Sub

        Public Sub ODEFunc(ByVal y As Double(), ByRef dy As Double())

            Dim conv As New SystemsOfUnits.Converter

            Dim i As Integer = 0
            Dim j As Integer = 0
            Dim scBC As Double = 0
            Dim BC As String = ""

            'conversion factors for different basis other than molar concentrations
            Dim convfactors As New Dictionary(Of String, Double)

            'loop through reactions
            Dim rxn As Reaction
            Dim ar As ArrayList = Me.ReactionsSequence(activeAL)

            i = 0
            Do
                'process reaction i
                rxn = Flowsheet.Reactions(ar(i))
                For Each sb As ReactionStoichBase In rxn.Components.Values
                    Ri(sb.CompName) = 0
                Next
                i += 1
            Loop Until i = ar.Count

            i = 0
            Do
                'process reaction i
                rxn = Flowsheet.Reactions(ar(i))
                BC = rxn.BaseReactant
                scBC = rxn.Components(BC).StoichCoeff

                j = 1
                For Each sb As ReactionStoichBase In rxn.Components.Values

                    C(sb.CompName) = y(j)
                    j = j + 1

                Next

                Dim T As Double = ims.Phases(0).Properties.temperature.GetValueOrDefault

                Dim rx As Double = 0.0#

                convfactors = Me.GetConvFactors(rxn, ims)

                If rxn.ReactionType = ReactionType.Kinetic Then

                    Dim kxf As Double = rxn.A_Forward * Exp(-rxn.E_Forward / (8.314 * T))
                    Dim kxr As Double = rxn.A_Reverse * Exp(-rxn.E_Reverse / (8.314 * T))

                    If T < rxn.Tmin Or T > rxn.Tmax Then
                        kxf = 0.0#
                        kxr = 0.0#
                    End If

                    Dim rxf As Double = 1.0#
                    Dim rxr As Double = 1.0#

                    'kinetic expression

                    For Each sb As ReactionStoichBase In rxn.Components.Values
                        rxf *= (C(sb.CompName) * convfactors(sb.CompName)) ^ sb.DirectOrder
                        rxr *= (C(sb.CompName) * convfactors(sb.CompName)) ^ sb.ReverseOrder
                    Next

                    rx = kxf * rxf - kxr * rxr
                    Rxi(rxn.ID) = SystemsOfUnits.Converter.ConvertToSI(rxn.VelUnit, rx)

                    Kf(i) = kxf
                    Kr(i) = kxr

                ElseIf rxn.ReactionType = ReactionType.Heterogeneous_Catalytic Then

                    Dim numval, denmval As Double

                    rxn.ExpContext = New Ciloci.Flee.ExpressionContext
                    rxn.ExpContext.Imports.AddType(GetType(System.Math))
                    rxn.ExpContext.Options.ParseCulture = Globalization.CultureInfo.InvariantCulture

                    rxn.ExpContext.Variables.Clear()
                    rxn.ExpContext.Variables.Add("T", T)

                    Dim ir As Integer = 1
                    Dim ip As Integer = 1

                    For Each sb As ReactionStoichBase In rxn.Components.Values
                        If sb.StoichCoeff < 0 Then
                            rxn.ExpContext.Variables.Add("R" & ir.ToString, C(sb.CompName) * convfactors(sb.CompName))
                            ir += 1
                        ElseIf sb.StoichCoeff > 0 Then
                            rxn.ExpContext.Variables.Add("P" & ip.ToString, C(sb.CompName) * convfactors(sb.CompName))
                            ip += 1
                        End If
                    Next

                    rxn.Expr = rxn.ExpContext.CompileGeneric(Of Double)(rxn.RateEquationNumerator)

                    numval = rxn.Expr.Evaluate

                    rxn.Expr = rxn.ExpContext.CompileGeneric(Of Double)(rxn.RateEquationDenominator)

                    denmval = rxn.Expr.Evaluate

                    rx = SystemsOfUnits.Converter.ConvertToSI(rxn.VelUnit, numval / denmval)

                End If

                For Each sb As ReactionStoichBase In rxn.Components.Values

                    If rxn.ReactionType = ReactionType.Kinetic Then
                        Ri(sb.CompName) += rx * sb.StoichCoeff / rxn.Components(BC).StoichCoeff
                    ElseIf rxn.ReactionType = ReactionType.Heterogeneous_Catalytic Then
                        Ri(sb.CompName) += rx * sb.StoichCoeff / rxn.Components(BC).StoichCoeff * Me.CatalystLoading
                    End If

                Next

                i += 1

            Loop Until i = ar.Count

            j = 1
            For Each kv As KeyValuePair(Of String, Double) In Ri

                dy(j) = -kv.Value '* Me.Volume
                j += 1

            Next

        End Sub

        Public Overrides Sub Calculate(Optional ByVal args As Object = Nothing)

            Dim conv As New SystemsOfUnits.Converter

            If Ri Is Nothing Then Ri = New Dictionary(Of String, Double)
            If Rxi Is Nothing Then Rxi = New Dictionary(Of String, Double)
            If DHRi Is Nothing Then DHRi = New Dictionary(Of String, Double)
            If DN Is Nothing Then DN = New Dictionary(Of String, Double)
            If N00 Is Nothing Then N00 = New Dictionary(Of String, Double)
            If Me.Conversions Is Nothing Then Me.m_conversions = New Dictionary(Of String, Double)
            If Me.ComponentConversions Is Nothing Then Me.m_componentconversions = New Dictionary(Of String, Double)

            points = New ArrayList

            If Not Me.GraphicObject.InputConnectors(0).IsAttached Then
                Throw New Exception(FlowSheet.GetTranslatedString("Nohcorrentedematriac16"))
            ElseIf Not Me.GraphicObject.OutputConnectors(0).IsAttached Then
                Throw New Exception(FlowSheet.GetTranslatedString("Nohcorrentedematriac15"))
            ElseIf Not Me.GraphicObject.InputConnectors(1).IsAttached Then
                Throw New Exception(FlowSheet.GetTranslatedString("Nohcorrentedeenerg17"))
            End If
            ims = DirectCast(FlowSheet.SimulationObjects(Me.GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name), MaterialStream).Clone
            pp = Me.PropertyPackage
            ppr = New PropertyPackages.RaoultPropertyPackage()

            ims.SetFlowsheet(Me.FlowSheet)

            Me.Reactions.Clear()
            Me.ReactionsSequence.Clear()
            Me.Conversions.Clear()
            Me.ComponentConversions.Clear()
            Me.DeltaQ = 0
            Me.DeltaT = 0
            Me.DN.Clear()

            'check active reactions (kinetic and heterogeneous only) in the reaction set
            For Each rxnsb As ReactionSetBase In FlowSheet.ReactionSets(Me.ReactionSetID).Reactions.Values
                If FlowSheet.Reactions(rxnsb.ReactionID).ReactionType = ReactionType.Kinetic And rxnsb.IsActive Then
                    Me.Reactions.Add(rxnsb.ReactionID)
                ElseIf FlowSheet.Reactions(rxnsb.ReactionID).ReactionType = ReactionType.Heterogeneous_Catalytic And rxnsb.IsActive Then
                    Me.Reactions.Add(rxnsb.ReactionID)
                End If
            Next

            'order reactions
            Dim i As Integer
            i = 0
            Dim maxrank As Integer = 0
            For Each rxnsb As ReactionSetBase In FlowSheet.ReactionSets(Me.ReactionSetID).Reactions.Values
                If rxnsb.Rank > maxrank And Me.Reactions.Contains(rxnsb.ReactionID) Then maxrank = rxnsb.Rank
            Next

            'ordering of parallel reactions
            i = 0
            Dim arr As New ArrayList
            Do
                arr = New ArrayList
                For Each rxnsb As ReactionSetBase In FlowSheet.ReactionSets(Me.ReactionSetID).Reactions.Values
                    If rxnsb.Rank = i And Me.Reactions.Contains(rxnsb.ReactionID) Then arr.Add(rxnsb.ReactionID)
                Next
                If arr.Count > 0 Then Me.ReactionsSequence.Add(i, arr)
                i = i + 1
            Loop Until i = maxrank + 1

            pp.CurrentMaterialStream = ims
            ppr.CurrentMaterialStream = ims

            Dim N0 As New Dictionary(Of String, Double)
            Dim N As New Dictionary(Of String, Double)
            Dim DNT As New Dictionary(Of String, Double)
            Dim DNj As New Dictionary(Of String, Double)
            N00.Clear()

            Dim scBC, DHr, Hid_r, Hid_p, Hr, Hr0, Hp, T, T0, P, P0, vol As Double
            Dim BC As String = ""
            Dim tmp As IFlashCalculationResult
            Dim maxXarr As New ArrayList

            'Reactants Enthalpy (kJ/kg * kg/s = kW) (ISOTHERMIC)
            Hr0 = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault

            T0 = ims.Phases(0).Properties.temperature.GetValueOrDefault
            P0 = ims.Phases(0).Properties.pressure.GetValueOrDefault

            'conversion factors for different basis other than molar concentrations
            Dim convfactors As New Dictionary(Of String, Double)

            'do the calculations on each dV
            Dim currvol As Double = 0
            Do

                C = New Dictionary(Of String, Double)
                C0 = New Dictionary(Of String, Double)

                Kf = New ArrayList(Me.Reactions.Count)
                Kr = New ArrayList(Me.Reactions.Count)

                T = ims.Phases(0).Properties.temperature.GetValueOrDefault
                P = ims.Phases(0).Properties.pressure.GetValueOrDefault

                'Reactants Enthalpy (kJ/kg * kg/s = kW)
                Hr = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault

                'loop through reactions
                Dim rxn As Reaction
                For Each ar As ArrayList In Me.ReactionsSequence.Values

                    i = 0
                    DHr = 0
                    Hid_r = 0
                    Hid_p = 0

                    Do

                        'process reaction i
                        rxn = Flowsheet.Reactions(ar(i))

                        'initial mole flows
                        For Each sb As ReactionStoichBase In rxn.Components.Values

                            Select Case rxn.ReactionPhase
                                Case PhaseName.Liquid
                                    If Not N0.ContainsKey(sb.CompName) Then
                                        N0.Add(sb.CompName, ims.Phases(3).Compounds(sb.CompName).MolarFlow.GetValueOrDefault)
                                        N00.Add(sb.CompName, N0(sb.CompName))
                                        N.Add(sb.CompName, N0(sb.CompName))
                                        C0.Add(sb.CompName, N0(sb.CompName) / ims.Phases(3).Properties.volumetric_flow.GetValueOrDefault)
                                    Else
                                        N0(sb.CompName) = ims.Phases(3).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                        N(sb.CompName) = N0(sb.CompName)
                                        C0(sb.CompName) = N0(sb.CompName) / ims.Phases(3).Properties.volumetric_flow.GetValueOrDefault
                                    End If
                                    vol = ims.Phases(3).Properties.volumetric_flow.GetValueOrDefault
                                Case PhaseName.Vapor
                                    If Not N0.ContainsKey(sb.CompName) Then
                                        N0.Add(sb.CompName, ims.Phases(2).Compounds(sb.CompName).MolarFlow.GetValueOrDefault)
                                        N00.Add(sb.CompName, N0(sb.CompName))
                                        N.Add(sb.CompName, N0(sb.CompName))
                                        C0.Add(sb.CompName, N0(sb.CompName) / ims.Phases(2).Properties.volumetric_flow.GetValueOrDefault)
                                    Else
                                        N0(sb.CompName) = ims.Phases(2).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                        N(sb.CompName) = N0(sb.CompName)
                                        C0(sb.CompName) = N0(sb.CompName) / ims.Phases(2).Properties.volumetric_flow.GetValueOrDefault
                                    End If
                                    vol = ims.Phases(2).Properties.volumetric_flow.GetValueOrDefault
                                Case PhaseName.Mixture
                                    If Not N0.ContainsKey(sb.CompName) Then
                                        N0.Add(sb.CompName, ims.Phases(0).Compounds(sb.CompName).MolarFlow.GetValueOrDefault)
                                        N00.Add(sb.CompName, N0(sb.CompName))
                                        N.Add(sb.CompName, N0(sb.CompName))
                                        C0.Add(sb.CompName, N0(sb.CompName) / ims.Phases(0).Properties.volumetric_flow.GetValueOrDefault)
                                    Else
                                        N0(sb.CompName) = ims.Phases(0).Compounds(sb.CompName).MolarFlow.GetValueOrDefault
                                        N(sb.CompName) = N0(sb.CompName)
                                        C0(sb.CompName) = N0(sb.CompName) / ims.Phases(0).Properties.volumetric_flow.GetValueOrDefault
                                    End If
                                    vol = ims.Phases(0).Properties.volumetric_flow.GetValueOrDefault
                            End Select

                        Next

                        i += 1

                    Loop Until i = ar.Count

                    Ri.Clear()
                    Rxi.Clear()

                    i = 0
                    Do

                        Dim rx As Double = 0.0#

                        'process reaction i

                        rxn = Flowsheet.Reactions(ar(i))
                        BC = rxn.BaseReactant
                        scBC = rxn.Components(BC).StoichCoeff

                        For Each sb As ReactionStoichBase In rxn.Components.Values

                            If C0(sb.CompName) = 0.0# Then C0(sb.CompName) = 0.0000000001
                            C(sb.CompName) = C0(sb.CompName)

                        Next

                        T = ims.Phases(0).Properties.temperature.GetValueOrDefault

                        convfactors = Me.GetConvFactors(rxn, ims)

                        If rxn.ReactionType = ReactionType.Kinetic Then

                            Dim kxf As Double = rxn.A_Forward * Exp(-rxn.E_Forward / (8.314 * T))
                            Dim kxr As Double = rxn.A_Reverse * Exp(-rxn.E_Reverse / (8.314 * T))

                            Dim rxf As Double = 1.0#
                            Dim rxr As Double = 1.0#

                            'kinetic expression

                            For Each sb As ReactionStoichBase In rxn.Components.Values
                                rxf *= (C(sb.CompName) * convfactors(sb.CompName)) ^ sb.DirectOrder
                                rxr *= (C(sb.CompName) * convfactors(sb.CompName)) ^ sb.ReverseOrder
                            Next

                            rx = SystemsOfUnits.Converter.ConvertToSI(rxn.VelUnit, kxf * rxf - kxr * rxr)

                            If Kf.Count - 1 <= i Then
                                Kf.Add(kxf)
                                Kr.Add(kxf)
                            Else
                                Kf(i) = kxf
                                Kr(i) = kxr
                            End If

                        ElseIf rxn.ReactionType = ReactionType.Heterogeneous_Catalytic Then

                            Dim numval, denmval As Double

                            rxn.ExpContext = New Ciloci.Flee.ExpressionContext
                            rxn.ExpContext.Imports.AddType(GetType(System.Math))
                            rxn.ExpContext.Options.ParseCulture = Globalization.CultureInfo.InvariantCulture

                            rxn.ExpContext.Variables.Clear()
                            rxn.ExpContext.Variables.Add("T", T)

                            Dim ir As Integer = 1
                            Dim ip As Integer = 1

                            For Each sb As ReactionStoichBase In rxn.Components.Values
                                If sb.StoichCoeff < 0 Then
                                    rxn.ExpContext.Variables.Add("R" & ir.ToString, C(sb.CompName) * convfactors(sb.CompName))
                                    ir += 1
                                ElseIf sb.StoichCoeff > 0 Then
                                    rxn.ExpContext.Variables.Add("P" & ip.ToString, C(sb.CompName) * convfactors(sb.CompName))
                                    ip += 1
                                End If
                            Next

                            Try
                                rxn.Expr = rxn.ExpContext.CompileGeneric(Of Double)(rxn.RateEquationNumerator)
                                numval = rxn.Expr.Evaluate
                            Catch ex As Exception
                                Throw New Exception(FlowSheet.GetTranslatedString("PFRNumeratorEvaluationError") & " " & rxn.Name)
                            End Try

                            Try
                                rxn.Expr = rxn.ExpContext.CompileGeneric(Of Double)(rxn.RateEquationDenominator)
                                denmval = rxn.Expr.Evaluate
                            Catch ex As Exception
                                Throw New Exception(FlowSheet.GetTranslatedString("PFRDenominatorEvaluationError") & " " & rxn.Name)
                            End Try

                            rx = SystemsOfUnits.Converter.ConvertToSI(rxn.VelUnit, numval / denmval)

                        End If

                        If Not Rxi.ContainsKey(rxn.ID) Then
                            Rxi.Add(rxn.ID, rx)
                        Else
                            Rxi(rxn.ID) = rx
                        End If

                        For Each sb As ReactionStoichBase In rxn.Components.Values

                            If Not Ri.ContainsKey(sb.CompName) Then
                                Ri.Add(sb.CompName, 0)
                            End If

                        Next

                        For Each sb As ReactionStoichBase In rxn.Components.Values

                            Ri(sb.CompName) += rx * sb.StoichCoeff / rxn.Components(BC).StoichCoeff

                        Next

                        i += 1

                    Loop Until i = ar.Count

                    '
                    '   SOLVE ODEs
                    '

                    Me.activeAL = Me.ReactionsSequence.IndexOfValue(ar)

                    Dim vc(C.Count) As Double
                    'vc(1) = Me.Volume
                    i = 1
                    For Each d As Double In C.Values
                        vc(i) = d
                        i = i + 1
                    Next

                    Dim bs As New MathEx.ODESolver.bulirschstoer
                    bs.DefineFuncDelegate(AddressOf ODEFunc)
                    bs.solvesystembulirschstoer(0, currvol, vc, Ri.Count, 0.05 * currvol, 0.001, True)

                    If Double.IsNaN(vc.Sum) Then Throw New Exception(FlowSheet.GetTranslatedString("PFRMassBalanceError"))

                    i = 1
                    For Each sb As KeyValuePair(Of String, Double) In C0
                        If Not DN.ContainsKey(sb.Key) Then
                            DNj.Add(sb.Key, vol * (vc(i) - C(sb.Key)))
                        Else
                            DNj(sb.Key) = vol * (vc(i) - C(sb.Key))
                        End If
                        i = i + 1
                    Next

                    C.Clear()
                    i = 1
                    For Each sb As KeyValuePair(Of String, Double) In C0
                        C(sb.Key) = Convert.ToDouble(vc(i))
                        i = i + 1
                    Next

                    i = 0
                    Do

                        'process reaction i
                        rxn = Flowsheet.Reactions(ar(i))
                        BC = rxn.BaseReactant
                        scBC = rxn.Components(BC).StoichCoeff

                        For Each sb As ReactionStoichBase In rxn.Components.Values

                            ''comp. conversions
                            If Not Me.ComponentConversions.ContainsKey(sb.CompName) Then
                                Me.ComponentConversions.Add(sb.CompName, 0)
                            End If

                        Next

                        i += 1

                    Loop Until i = ar.Count

                    For Each sb As String In Me.ComponentConversions.Keys
                        N(sb) = N0(sb) * (1 - (C0(sb) - C(sb)) / C0(sb))
                    Next

                    For Each sb As String In Me.ComponentConversions.Keys
                        If Not DN.ContainsKey(sb) Then
                            DN.Add(sb, N(sb) - N0(sb))
                        Else
                            DN(sb) = N(sb) - N0(sb)
                        End If
                    Next

                    For Each sb As String In Me.ComponentConversions.Keys
                        If Not DNT.ContainsKey(sb) Then
                            DNT.Add(sb, DN(sb))
                        Else
                            DNT(sb) += DN(sb)
                        End If
                    Next

                    'Ideal Gas Reactants Enthalpy (kJ/kg * kg/s = kW)
                    Hid_r += 0 'ppr.RET_Hid(298.15, ims.Phases(0).Properties.temperature.GetValueOrDefault, PropertyPackages.Phase.Mixture) * ims.Phases(0).Properties.massflow.GetValueOrDefault

                    Dim NS As New Dictionary(Of String, Double)

                    i = 0
                    Do

                        'process reaction i
                        rxn = Flowsheet.Reactions(ar(i))

                        If NS.ContainsKey(rxn.BaseReactant) Then
                            NS(rxn.BaseReactant) += Rxi(rxn.ID)
                        Else
                            NS.Add(rxn.BaseReactant, Rxi(rxn.ID))
                        End If

                        i += 1

                    Loop Until i = ar.Count

                    DHRi.Clear()

                    i = 0
                    Do

                        'process reaction i
                        rxn = Flowsheet.Reactions(ar(i))

                        'Heat released (or absorbed) (kJ/s = kW) (Ideal Gas)
                        Select Case Me.ReactorOperationMode
                            Case OperationMode.Adiabatic
                                DHr = rxn.ReactionHeat * Abs(DNj(rxn.BaseReactant)) / 1000 * Rxi(rxn.ID) / Ri(rxn.BaseReactant)
                            Case OperationMode.Isothermic
                                DHr += rxn.ReactionHeat * Abs(DNj(rxn.BaseReactant)) / 1000 * Rxi(rxn.ID) / Ri(rxn.BaseReactant)
                        End Select

                        DHRi.Add(rxn.ID, DHr)

                        i += 1

                    Loop Until i = ar.Count

                    'update mole flows/fractions
                    Dim Nsum As Double = 0

                    'compute new mole flows
                    'Nsum = ims.Phases(0).Properties.molarflow.GetValueOrDefault
                    For Each s2 As Compound In ims.Phases(0).Compounds.Values
                        If DN.ContainsKey(s2.Name) Then
                            Nsum += N(s2.Name)
                        Else
                            Nsum += s2.MolarFlow.GetValueOrDefault
                        End If
                    Next
                    For Each s2 As Compound In ims.Phases(0).Compounds.Values
                        If DN.ContainsKey(s2.Name) Then
                            s2.MoleFraction = (ims.Phases(0).Compounds(s2.Name).MolarFlow.GetValueOrDefault + DN(s2.Name)) / Nsum
                            s2.MolarFlow = ims.Phases(0).Compounds(s2.Name).MolarFlow.GetValueOrDefault + DN(s2.Name)
                        Else
                            s2.MoleFraction = ims.Phases(0).Compounds(s2.Name).MolarFlow.GetValueOrDefault / Nsum
                            s2.MolarFlow = ims.Phases(0).Compounds(s2.Name).MolarFlow.GetValueOrDefault
                        End If
                    Next

                    ims.Phases(0).Properties.molarflow = Nsum

                    Dim mmm As Double = 0
                    Dim mf As Double = 0
                    For Each s3 As Compound In ims.Phases(0).Compounds.Values
                        mmm += s3.MoleFraction.GetValueOrDefault * s3.ConstantProperties.Molar_Weight
                    Next
                    For Each s3 As Compound In ims.Phases(0).Compounds.Values
                        s3.MassFraction = s3.MoleFraction.GetValueOrDefault * s3.ConstantProperties.Molar_Weight / mmm
                        s3.MassFlow = s3.MassFraction.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault
                        mf += s3.MassFlow.GetValueOrDefault
                    Next

                    'Ideal Gas Products Enthalpy (kJ/kg * kg/s = kW)
                    Hid_p += 0 'ppr.RET_Hid(298.15, ims.Phases(0).Properties.temperature.GetValueOrDefault, PropertyPackages.Phase.Mixture) * ims.Phases(0).Properties.massflow.GetValueOrDefault

                    'do a flash calc (calculate final temperature/enthalpy)

                    Me.PropertyPackage.CurrentMaterialStream = ims

                    Select Case Me.ReactorOperationMode

                        Case OperationMode.Adiabatic

                            Me.DeltaQ = GetInletEnergyStream(1).EnergyFlow.GetValueOrDefault

                            'Products Enthalpy (kJ/kg * kg/s = kW)
                            Hp = Me.dV * Me.DeltaQ.GetValueOrDefault + Hr + Hid_p - Hid_r - DHr

                            tmp = Me.PropertyPackage.CalculateEquilibrium2(FlashCalculationType.PressureEnthalpy, P, Hp / ims.Phases(0).Properties.massflow.GetValueOrDefault, T)
                            Dim Tout As Double = tmp.CalculatedTemperature

                            Me.DeltaT = Me.DeltaT.GetValueOrDefault + Tout - T
                            ims.Phases(0).Properties.temperature = Tout
                            T = ims.Phases(0).Properties.temperature.GetValueOrDefault

                        Case OperationMode.Isothermic

                        Case OperationMode.OutletTemperature

                            Me.DeltaQ = GetInletEnergyStream(1).EnergyFlow.GetValueOrDefault

                            'Products Enthalpy (kJ/kg * kg/s = kW)
                            Hp = Me.dV * Me.DeltaQ.GetValueOrDefault + Hr + Hid_p - Hid_r - DHr

                            Me.DeltaT = Me.DeltaT.GetValueOrDefault + Me.OutletTemperature - T
                            ims.Phases(0).Properties.temperature = Me.OutletTemperature
                            T = ims.Phases(0).Properties.temperature.GetValueOrDefault

                    End Select

                    ims.Calculate(True, True)

                Next

                ' comp. conversions
                For Each sb As Compound In ims.Phases(0).Compounds.Values
                    If Me.ComponentConversions.ContainsKey(sb.Name) Then
                        Me.ComponentConversions(sb.Name) += -DN(sb.Name) / N00(sb.Name)
                    End If
                Next

                'add data to array
                Dim tmparr(C.Count + 2) As Double
                tmparr(0) = currvol
                i = 1
                For Each d As Double In Me.C.Values
                    tmparr(i) = d
                    i = i + 1
                Next
                tmparr(i) = T
                tmparr(i + 1) = P

                Me.points.Add(tmparr)

                Dim Qvin, Qlin, eta_v, eta_l, rho_v, rho_l, tens, q, rho, eta As Double

                With ims
                    q = .Phases(0).Properties.volumetric_flow.GetValueOrDefault
                    rho = .Phases(0).Properties.density.GetValueOrDefault
                    eta = .Phases(0).Properties.viscosity.GetValueOrDefault
                    Qlin = .Phases(1).Properties.volumetric_flow.GetValueOrDefault
                    rho_l = .Phases(1).Properties.density.GetValueOrDefault
                    eta_l = .Phases(1).Properties.viscosity.GetValueOrDefault
                    tens = .Phases(0).Properties.surfaceTension.GetValueOrDefault
                    Qvin = .Phases(2).Properties.volumetric_flow.GetValueOrDefault
                    rho_v = .Phases(2).Properties.density.GetValueOrDefault
                    eta_v = .Phases(2).Properties.viscosity.GetValueOrDefault
                End With

                Dim diameter As Double = (4 * Me.Volume / PI / Me.Length) ^ 0.5

                If Me.CatalystLoading = 0.0# Then

                    Dim resv As Object
                    Dim fpp As New DWSIM.FlowPackages.BeggsBrill
                    Dim tipofluxo As String, holdup, dpf, dph, dpt As Double

                    resv = fpp.CalculateDeltaP(diameter * 0.0254, Me.dV * Me.Length, 0.0#, 0.000045, Qvin * 24 * 3600, Qlin * 24 * 3600, eta_v * 1000, eta_l * 1000, rho_v, rho_l, tens)

                    tipofluxo = resv(0)
                    holdup = resv(1)
                    dpf = resv(2)
                    dph = resv(3)
                    dpt = resv(4)

                    P -= dpf

                Else

                    'has catalyst, use Ergun equation for pressure drop in reactor beds

                    Dim vel As Double = q / (PI * diameter ^ 2 / 4)
                    Dim L As Double = Me.dV * Me.Length
                    Dim dp As Double = Me.CatalystParticleDiameter
                    Dim ev As Double = Me.CatalystVoidFraction

                    Dim pdrop As Double = 150 * eta * L / dp ^ 2 * (1 - ev) ^ 2 / ev ^ 3 * vel + 1.75 * L * rho / dp * (1 - ev) / ev ^ 3 * vel ^ 2

                    P -= pdrop

                End If

                If P < 0 Then Throw New Exception(FlowSheet.GetTranslatedString("PFRNegativePressureError"))

                ims.Phases(0).Properties.pressure = P

                currvol += Me.dV * Me.Volume

            Loop Until currvol - Me.Volume > dV * Me.Volume * 0.98

            Me.DeltaP = P0 - P

            If Me.ReactorOperationMode = OperationMode.Isothermic Then

                'Products Enthalpy (kJ/kg * kg/s = kW)
                Hp = ims.Phases(0).Properties.enthalpy.GetValueOrDefault * ims.Phases(0).Properties.massflow.GetValueOrDefault
                'Heat (kW)
                Me.DeltaQ = DHr + Hp - Hr0
                Me.DeltaT = 0

            End If

            Dim ms As MaterialStream
            Dim cp As ConnectionPoint
            Dim mtotal, wtotal As Double

            cp = Me.GraphicObject.OutputConnectors(0)
            If cp.IsAttached Then
                ms = Flowsheet.SimulationObjects(cp.AttachedConnector.AttachedTo.Name)
                With ms
                    .Phases(0).Properties.massflow = ims.Phases(0).Properties.massflow.GetValueOrDefault
                    .Phases(0).Properties.massfraction = 1
                    .Phases(0).Properties.temperature = ims.Phases(0).Properties.temperature.GetValueOrDefault
                    .Phases(0).Properties.pressure = ims.Phases(0).Properties.pressure.GetValueOrDefault
                    .Phases(0).Properties.enthalpy = ims.Phases(0).Properties.enthalpy.GetValueOrDefault
                    Dim comp As BaseClasses.Compound
                    mtotal = 0
                    wtotal = 0
                    For Each comp In .Phases(0).Compounds.Values
                        mtotal += ims.Phases(0).Compounds(comp.Name).MoleFraction.GetValueOrDefault
                        wtotal += ims.Phases(0).Compounds(comp.Name).MassFraction.GetValueOrDefault
                    Next
                    For Each comp In .Phases(0).Compounds.Values
                        comp.MoleFraction = ims.Phases(0).Compounds(comp.Name).MoleFraction.GetValueOrDefault / mtotal
                        comp.MassFraction = ims.Phases(0).Compounds(comp.Name).MassFraction.GetValueOrDefault / wtotal
                        comp.MassFlow = comp.MassFraction.GetValueOrDefault * .Phases(0).Properties.massflow.GetValueOrDefault
                        comp.MolarFlow = comp.MoleFraction.GetValueOrDefault * .Phases(0).Properties.molarflow.GetValueOrDefault
                    Next
                End With
            End If

            'Corrente de EnergyFlow - atualizar valor da potencia (kJ/s)
            Dim estr As Streams.EnergyStream = Flowsheet.SimulationObjects(Me.GraphicObject.InputConnectors(1).AttachedConnector.AttachedFrom.Name)
            With estr
                .EnergyFlow = Me.DeltaQ.GetValueOrDefault
                .GraphicObject.Calculated = True
            End With

        End Sub

        Public Overrides Sub DeCalculate()

            'If Not Me.GraphicObject.InputConnectors(0).IsAttached Then Throw New Exception(Flowsheet.GetTranslatedString("Nohcorrentedematriac10"))
            'If Not Me.GraphicObject.OutputConnectors(0).IsAttached Then Throw New Exception(Flowsheet.GetTranslatedString("Nohcorrentedematriac11"))
            'If Not Me.GraphicObject.OutputConnectors(1).IsAttached Then Throw New Exception(Flowsheet.GetTranslatedString("Nohcorrentedematriac11"))

            'Dim ems As DWSIM.SimulationObjects.Streams.MaterialStream = Flowsheet.SimulationObjects(Me.GraphicObject.InputConnectors(0).AttachedConnector.AttachedFrom.Name)
            'Dim W As Double = ems.Phases(0).Properties.massflow.GetValueOrDefault
            Dim j As Integer = 0

            Dim ms As MaterialStream
            Dim cp As ConnectionPoint

            cp = Me.GraphicObject.OutputConnectors(0)
            If cp.IsAttached Then
                ms = Flowsheet.SimulationObjects(cp.AttachedConnector.AttachedTo.Name)
                With ms
                    .Phases(0).Properties.temperature = Nothing
                    .Phases(0).Properties.pressure = Nothing
                    .Phases(0).Properties.enthalpy = Nothing
                    Dim comp As BaseClasses.Compound
                    j = 0
                    For Each comp In .Phases(0).Compounds.Values
                        comp.MoleFraction = 0
                        comp.MassFraction = 0
                        j += 1
                    Next
                    .Phases(0).Properties.massflow = Nothing
                    .Phases(0).Properties.massfraction = 1
                    .Phases(0).Properties.molarfraction = 1
                    .GraphicObject.Calculated = False
                End With
            End If

        End Sub

        Public Overrides Function GetPropertyValue(ByVal prop As String, Optional ByVal su As Interfaces.IUnitsOfMeasure = Nothing) As Object
            If su Is Nothing Then su = New SystemsOfUnits.SI
            Dim cv As New SystemsOfUnits.Converter
            Dim value As Double = 0
            Dim propidx As Integer = Convert.ToInt32(prop.Split("_")(2))

            Select Case propidx

                Case 0
                    'PROP_HT_0	Pressure Drop
                    value = SystemsOfUnits.Converter.ConvertFromSI(su.deltaP, Me.DeltaP.GetValueOrDefault)

            End Select

            Return value
        End Function

        Public Overloads Overrides Function GetProperties(ByVal proptype As Interfaces.Enums.PropertyType) As String()
            Dim i As Integer = 0
            Dim proplist As New ArrayList
            Select Case proptype
                Case PropertyType.RW
                    For i = 0 To 0
                        proplist.Add("PROP_PF_" + CStr(i))
                    Next
                Case PropertyType.WR
                    For i = 0 To 0
                        proplist.Add("PROP_PF_" + CStr(i))
                    Next
                Case PropertyType.ALL
                    For i = 0 To 0
                        proplist.Add("PROP_PF_" + CStr(i))
                    Next
            End Select
            Return proplist.ToArray(GetType(System.String))
            proplist = Nothing
        End Function

        Public Overrides Function SetPropertyValue(ByVal prop As String, ByVal propval As Object, Optional ByVal su As Interfaces.IUnitsOfMeasure = Nothing) As Boolean
            If su Is Nothing Then su = New SystemsOfUnits.SI
            Dim cv As New SystemsOfUnits.Converter
            Dim propidx As Integer = Convert.ToInt32(prop.Split("_")(2))

            Select Case propidx

                Case 0
                    'PROP_HT_0	Pressure Drop
                    Me.DeltaP = SystemsOfUnits.Converter.ConvertToSI(su.deltaP, propval)

            End Select
            Return 1
        End Function

        Public Overrides Function GetPropertyUnit(ByVal prop As String, Optional ByVal su As Interfaces.IUnitsOfMeasure = Nothing) As String
            If su Is Nothing Then su = New SystemsOfUnits.SI
            Dim cv As New SystemsOfUnits.Converter
            Dim value As String = ""
            Dim propidx As Integer = Convert.ToInt32(prop.Split("_")(2))

            Select Case propidx

                Case 0
                    'PROP_HT_0	Pressure Drop
                    value = su.deltaP

            End Select

            Return value
        End Function

        Public Overrides Sub DisplayEditForm()

        End Sub
    End Class

End Namespace



