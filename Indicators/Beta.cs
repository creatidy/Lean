/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using QuantConnect.Data.Market;
using MathNet.Numerics.Statistics;
using QuantConnect.Securities;
using NodaTime;

namespace QuantConnect.Indicators
{
    /// <summary>
    /// In technical analysis Beta indicator is used to measure volatility or risk of a target (ETF) relative to the overall 
    /// risk (volatility) of the reference (market indexes). The Beta indicators compares target's price movement to the 
    /// movements of the indexes over the same period of time.
    /// 
    /// It is common practice to use the SPX index as a benchmark of the overall reference market when it comes to Beta 
    /// calculations.
    /// 
    /// The indicator only updates when both assets have a price for a time step. When a bar is missing for one of the assets, 
    /// the indicator value fills forward to improve the accuracy of the indicator.
    /// </summary>
    public class Beta : BarIndicator, IIndicatorWarmUpPeriodProvider
    {
        /// <summary>
        /// RollingWindow to store the data points of the target symbol
        /// </summary>
        private readonly RollingWindow<decimal> _targetDataPoints;

        /// <summary>
        /// RollingWindow to store the data points of the reference symbol
        /// </summary>
        private readonly RollingWindow<decimal> _referenceDataPoints;

        /// <summary>
        /// Symbol of the reference used
        /// </summary>
        private readonly Symbol _referenceSymbol;

        /// <summary>
        /// Symbol of the target used
        /// </summary>
        private readonly Symbol _targetSymbol;

        /// <summary>
        /// Stores the previous input data point.
        /// </summary>
        private IBaseDataBar _previousInput;

        /// <summary>
        /// Indicates whether the previous symbol is the target symbol.
        /// </summary>
        private bool _previousSymbolIsTarget;

        /// <summary>
        /// Indicates if the time zone for the target and reference are different.
        /// </summary>
        private bool _isTimezoneDifferent;

        /// <summary>
        /// Time zone of the target symbol.
        /// </summary>
        private DateTimeZone _targetTimeZone;

        /// <summary>
        /// Time zone of the reference symbol.
        /// </summary>
        private DateTimeZone _referenceTimeZone;

        /// <summary>
        /// The resolution of the data (e.g., daily, hourly, etc.).
        /// </summary>
        private Resolution _resolution;

        /// <summary>
        /// RollingWindow of returns of the target symbol in the given period
        /// </summary>
        private readonly RollingWindow<double> _targetReturns;

        /// <summary>
        /// RollingWindow of returns of the reference symbol in the given period
        /// </summary>
        private readonly RollingWindow<double> _referenceReturns;

        /// <summary>
        /// Beta of the target used in relation with the reference
        /// </summary>
        private decimal _beta;

        /// <summary>
        /// Required period, in data points, for the indicator to be ready and fully initialized.
        /// </summary>
        public int WarmUpPeriod { get; private set; }

        /// <summary>
        /// Gets a flag indicating when the indicator is ready and fully initialized
        /// </summary>
        public override bool IsReady => _targetReturns.IsReady && _referenceReturns.IsReady;

        /// <summary>
        /// Creates a new Beta indicator with the specified name, target, reference,  
        /// and period values
        /// </summary>
        /// <param name="name">The name of this indicator</param>
        /// <param name="targetSymbol">The target symbol of this indicator</param>
        /// <param name="period">The period of this indicator</param>
        /// <param name="referenceSymbol">The reference symbol of this indicator</param>
        public Beta(string name, Symbol targetSymbol, Symbol referenceSymbol, int period)
            : base(name)
        {
            // Assert the period is greater than two, otherwise the beta can not be computed
            if (period < 2)
            {
                throw new ArgumentException($"Period parameter for Beta indicator must be greater than 2 but was {period}.");
            }
            _referenceSymbol = referenceSymbol;
            _targetSymbol = targetSymbol;

            _targetDataPoints = new RollingWindow<decimal>(2);
            _referenceDataPoints = new RollingWindow<decimal>(2);

            _targetReturns = new RollingWindow<double>(period);
            _referenceReturns = new RollingWindow<double>(period);
            _beta = 0;
            var dataFolder = MarketHoursDatabase.FromDataFolder();
            _targetTimeZone = dataFolder.GetExchangeHours(_targetSymbol.ID.Market, _targetSymbol, _targetSymbol.ID.SecurityType).TimeZone;
            _referenceTimeZone = dataFolder.GetExchangeHours(_referenceSymbol.ID.Market, _referenceSymbol, _referenceSymbol.ID.SecurityType).TimeZone;
            _isTimezoneDifferent = _targetTimeZone != _referenceTimeZone;
            WarmUpPeriod = period + 1 + (_isTimezoneDifferent ? 1 : 0);
        }

        /// <summary>
        /// Creates a new Beta indicator with the specified target, reference,  
        /// and period values
        /// </summary>
        /// <param name="targetSymbol">The target symbol of this indicator</param>
        /// <param name="period">The period of this indicator</param>
        /// <param name="referenceSymbol">The reference symbol of this indicator</param>
        public Beta(Symbol targetSymbol, Symbol referenceSymbol, int period)
            : this($"B({period})", targetSymbol, referenceSymbol, period)
        {
        }

        /// <summary>
        /// Creates a new Beta indicator with the specified name, period, target and 
        /// reference values
        /// </summary>
        /// <param name="name">The name of this indicator</param>
        /// <param name="period">The period of this indicator</param>
        /// <param name="targetSymbol">The target symbol of this indicator</param>
        /// <param name="referenceSymbol">The reference symbol of this indicator</param>
        /// <remarks>Constructor overload for backward compatibility.</remarks>
        public Beta(string name, int period, Symbol targetSymbol, Symbol referenceSymbol)
            : this(name, targetSymbol, referenceSymbol, period)
        {
        }

        /// <summary>
        /// Computes the next value for this indicator from the given state.
        /// 
        /// As this indicator is receiving data points from two different symbols,
        /// it's going to compute the next value when the amount of data points
        /// of each of them is the same. Otherwise, it will return the last beta
        /// value computed
        /// </summary>
        /// <param name="input">The input value of this indicator on this time step.
        /// It can be either from the target or the reference symbol</param>
        /// <returns>The beta value of the target used in relation with the reference</returns>
        protected override decimal ComputeNextValue(IBaseDataBar input)
        {
            if (_previousInput == null)
            {
                _previousInput = input;
                _previousSymbolIsTarget = input.Symbol == _targetSymbol;
                var timeDifference = input.EndTime - input.Time;
                _resolution = timeDifference.TotalHours > 1 ? Resolution.Daily : timeDifference.ToHigherResolutionEquivalent(false);
                return decimal.Zero;
            }

            var inputEndTime = input.EndTime;
            var previousInputEndTime = _previousInput.EndTime;

            if (_isTimezoneDifferent)
            {
                inputEndTime = inputEndTime.ConvertToUtc(_previousSymbolIsTarget ? _referenceTimeZone : _targetTimeZone);
                previousInputEndTime = previousInputEndTime.ConvertToUtc(_previousSymbolIsTarget ? _targetTimeZone : _referenceTimeZone);
            }

            // Process data if symbol has changed and timestamps match
            if (input.Symbol != _previousInput.Symbol && TruncateToResolution(inputEndTime) == TruncateToResolution(previousInputEndTime))
            {
                AddDataPoint(input);
                AddDataPoint(_previousInput);
                ComputeBeta();
            }
            _previousInput = input;
            _previousSymbolIsTarget = input.Symbol == _targetSymbol;
            return _beta;
        }

        /// <summary>
        /// Truncates the given DateTime based on the specified resolution (Daily, Hourly, Minute, or Second).
        /// </summary>
        /// <param name="date">The DateTime to truncate.</param>
        /// <returns>A DateTime truncated to the specified resolution.</returns>
        private DateTime TruncateToResolution(DateTime date)
        {
            switch (_resolution)
            {
                case Resolution.Daily:
                    return date.Date;
                case Resolution.Hour:
                    return date.Date.AddHours(date.Hour);
                case Resolution.Minute:
                    return date.Date.AddHours(date.Hour).AddMinutes(date.Minute);
                case Resolution.Second:
                    return date;
                default:
                    return date;
            }
        }

        /// <summary>
        /// Adds the closing price to the corresponding symbol's data set (target or reference).
        /// Computes returns when there are enough data points for each symbol.
        /// </summary>
        /// <param name="input">The input value for this symbol</param>
        private void AddDataPoint(IBaseDataBar input)
        {
            if (input.Symbol == _targetSymbol)
            {
                _targetDataPoints.Add(input.Close);
                if (_targetDataPoints.Count > 1)
                {
                    _targetReturns.Add(GetNewReturn(_targetDataPoints));
                }
            }
            else if (input.Symbol == _referenceSymbol)
            {
                _referenceDataPoints.Add(input.Close);
                if (_referenceDataPoints.Count > 1)
                {
                    _referenceReturns.Add(GetNewReturn(_referenceDataPoints));
                }
            }
            else
            {
                throw new ArgumentException($"The given symbol {input.Symbol} was not {_targetSymbol} or {_referenceSymbol} symbol");
            }
        }

        /// <summary>
        /// Computes the returns with the new given data point and the last given data point
        /// </summary>
        /// <param name="rollingWindow">The collection of data points from which we want
        /// to compute the return</param>
        /// <returns>The returns with the new given data point</returns>
        private static double GetNewReturn(RollingWindow<decimal> rollingWindow)
        {
            return (double)((rollingWindow[0].SafeDivision(rollingWindow[1]) - 1));
        }

        /// <summary>
        /// Computes the beta value of the target in relation with the reference
        /// using the target and reference returns
        /// </summary>
        private void ComputeBeta()
        {
            var varianceComputed = _referenceReturns.Variance();
            var covarianceComputed = _targetReturns.Covariance(_referenceReturns);

            // Avoid division with NaN or by zero
            var variance = !varianceComputed.IsNaNOrZero() ? varianceComputed : 1;
            var covariance = !covarianceComputed.IsNaNOrZero() ? covarianceComputed : 0;
            _beta = (decimal)(covariance / variance);
        }

        /// <summary>
        /// Resets this indicator to its initial state
        /// </summary>
        public override void Reset()
        {
            _previousInput = null;
            _targetDataPoints.Reset();
            _referenceDataPoints.Reset();
            _targetReturns.Reset();
            _referenceReturns.Reset();
            _beta = 0;
            base.Reset();
        }
    }
}
