using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Web;
using System.Xml.XPath;
using TeaCommerce.Data;
using TeaCommerce.Data.Extensibility;
using TeaCommerce.Data.Tools;
using umbraco.BusinessLogic;
using umbraco.cms.businesslogic.web;

/// <summary>
/// Summary description for TeaCommerceExtension
/// </summary>
namespace TeaCommerce.WebShop.Integration
{
    public class TeaCommerceExtension : ITeaCommerceExtension
    {
        // Helpful Logging
        public Dictionary<LogTypes, Action<string>> LogMessage = new Dictionary<LogTypes, Action<string>>
        {
            {LogTypes.Error, (message) => Log.Add(LogTypes.Error, User.GetUser(0), -1, message)},
            {LogTypes.Debug, (message) => Log.Add(LogTypes.Debug, User.GetUser(0), -1, message)}
        };

        // Various event handlers used thoughout the shop to call a shipping update
        #region ITeaCommerceExtension Members

        public void Initialize()
        {
            WebshopEvents.OrderLineChanged += WebshopEvents_OrderLineChanged;
            WebshopEvents.ShippingMethodChanged += WebshopEvents_ShippingMethodChanged;
            WebshopEvents.CurrencyChanged += WebshopEvents_CurrencyChanged;
            WebshopEvents.OrderLineRemoved += WebshopEvents_OrderLineRemoved;
            WebshopEvents.CountryChanged += WebshopEvents_CountryChanged;
        }

        void WebshopEvents_OrderLineRemoved(Order order, OrderLine orderLine)
        {
            UpdateOrderShippingCost(order);
        }

        /// <summary>
        /// On currency change
        /// </summary>
        /// <param name="order"></param>
        /// <param name="currency"></param>
        void WebshopEvents_CurrencyChanged(Order order, Currency currency)
        {
            UpdateOrderShippingCost(order);
        }

        /// <summary>
        /// On shipping method change
        /// </summary>
        /// <param name="order"></param>
        /// <param name="shippingMethod"></param>
        void WebshopEvents_ShippingMethodChanged(Order order, ShippingMethod shippingMethod)
        {
            UpdateOrderShippingCost(order);
        }

        /// <summary>
        /// On country change
        /// </summary>
        /// <param name="order"></param>
        /// <param name="country"></param>
        void WebshopEvents_CountryChanged(Order order, Country country)
        {
            UpdateOrderShippingCost(order);

            umbraco.BusinessLogic.Log.Add(umbraco.BusinessLogic.LogTypes.Error,
                    umbraco.BusinessLogic.User.GetUser(0), -1,
                    "changed country to: " + country.Name + " and order country is " + order.Country.Name);
        }

        void WebshopEvents_OrderLineChanged(Order order, OrderLine orderLine)
        {
            UpdateOrderShippingCost(order);
        }

        #endregion

        // If debug, values will be added to database log table
        Boolean debug = false;
        string version = "v1.2 - 07-08-2013";

        // Is called every time the order changes
        private void UpdateOrderShippingCost(Order order)
        {
            // Get the shipping rules node and the defaults from it
            Document shippingRules = new Document(1239);
            decimal? defaultWeightInKg = shippingRules.getProperty("defaultWeight").Value == null ? 0.0M : PriceHelper.ParsePrice(shippingRules.getProperty("defaultWeight").Value.ToString());
            decimal? defaultCost = shippingRules.getProperty("defaultCost").Value == null ? 0.0M : PriceHelper.ParsePrice(shippingRules.getProperty("defaultCost").Value.ToString());
            Boolean isPerKg = shippingRules.getProperty("isPerKG").Value != null && shippingRules.getProperty("isPerKG").Value.ToString() == "1" ? true : false;
            
            // Total weight of the order
            decimal? totalWeightInKg = 0.0M;

            // Cost of the shipping for this order
            decimal? shippingCostWithoutVAT = defaultCost;

            // Countries the shipping rules apply to
            Document[] countries = shippingRules.Children;

            // foundMatch used to determin if over weight should be used
            Boolean foundMatch = false;

            // if no over weight is used after no match has been found, use this to show warning message
            Boolean overWeight = false;

            // If debug, log an error in the database
            if (debug)
            {
                LogMessage[LogTypes.Error]("Default weight: " + defaultWeightInKg + " and default price per kg: " + defaultCost);
            }

            // Navigate through each order line, one order line may contain multiple products
            foreach (OrderLine ol in order.OrderLines)
            {
                // Get the order line weight
                OrderLineProperty weightProp = ol.Properties.FirstOrDefault(p => p.Alias == "productWeight");

                if (weightProp != null && !String.IsNullOrEmpty(weightProp.Value))
                {
                    // Add the order line weight to the total weight
                    totalWeightInKg += PriceHelper.ParsePrice(weightProp.Value.ToString()) * ol.Quantity;
                    
                    if (debug)
                    {
                        LogMessage[LogTypes.Error]("Adding " + weightProp.Value.ToString() + " to total weight");
                    }
                }
                else
                {
                    // Add the default weight to the total weight because this order line has no weight
                    totalWeightInKg += defaultWeightInKg * ol.Quantity;
                }
            }

            // If debug, log an error in the database
            if (debug)
            {
                LogMessage[LogTypes.Error]("Total weight in kg: " + totalWeightInKg);
            }

            // Round up total weight because shipping doesn't cover half kg
            totalWeightInKg = Math.Ceiling((decimal)totalWeightInKg);

            // Navigate through the countries if they exist
            if (shippingRules.Children.Length > 0)
            {
                foreach (Document country in countries)
                {
                    if (debug)
                    {
                        LogMessage[LogTypes.Error]("Found country: " + country.Text);
                    }

                    // Find one that matches the order country
                    if (country.Text == order.Country.Name)
                    {
                        // If debug, log an error in the database
                        if (debug)
                        {
                            LogMessage[LogTypes.Error]("Country match: " + country.Text);
                        }

                        // Get the weight rules
                        Document[] weightRules = country.Children;

                        // Navigate through the weight rules if they exist
                        if (country.Children.Length > 0)
                        {
                            foreach (Document weightRule in weightRules)
                            {
                                // If debug, log an error in the database
                                if (debug)
                                {
                                    LogMessage[LogTypes.Error]("Found rule: up to " + weightRule.getProperty("upToWeight").Value.ToString() + " checking against: " + totalWeightInKg);

                                    LogMessage[LogTypes.Error]("Match result is " + (decimal.Parse(weightRule.getProperty("upToWeight").Value.ToString()) >= totalWeightInKg));
                                }

                                // If this rules up to weight is equal to or greater than the order weight then rule matches
                                if (decimal.Parse(weightRule.getProperty("upToWeight").Value.ToString()) >= totalWeightInKg)
                                {
                                    // order shipping cost set to rules cost unless the 'is per kg' override has been set
                                    if (!isPerKg)
                                    {
                                        shippingCostWithoutVAT = PriceHelper.ParsePrice(weightRule.getProperty("fixedCost").Value.ToString());
                                    }
                                    else
                                    {
                                        shippingCostWithoutVAT = PriceHelper.ParsePrice(weightRule.getProperty("fixedCost").Value.ToString()) * totalWeightInKg;
                                    }

                                    // Match has been located
                                    foundMatch = true;

                                    if (debug)
                                    {
                                        LogMessage[LogTypes.Error]("match rule: " + weightRule.getProperty("upToWeight").Value.ToString());
                                    }

                                    break;
                                }
                            }
                            
                            // If match hasn't been located, use over weight of last rule providing over weight is enabled
                            if (!foundMatch && weightRules.Last().getProperty("overWeight").Value.ToString() == "1")
                            {
                                overWeight = true;

                                // If debug, log an error in the database
                                if (debug)
                                {
                                    LogMessage[LogTypes.Error]("No shipping rules matched order weight, using over weight of last rule over weight is " + shippingCostWithoutVAT);
                                }

                                // work out shipping if last rule has can go over weight set and no matching rules can be found
                                if (!isPerKg)
                                {
                                    // Calculate over weight
                                    shippingCostWithoutVAT = PriceHelper.ParsePrice(weightRules.Last().getProperty("fixedCost").Value.ToString())
                                        + (PriceHelper.ParsePrice(weightRules.Last().getProperty("overWeightCost").Value.ToString())
                                        * (totalWeightInKg - Decimal.Parse(weightRules.Last().getProperty("upToWeight").Value.ToString())));
                                }
                                else
                                {
                                    // Just do per kg if override has been set
                                    shippingCostWithoutVAT = PriceHelper.ParsePrice(weightRules.Last().getProperty("fixedCost").Value.ToString())
                                        * totalWeightInKg;
                                }
                            }

                        }
                        else
                        {
                            // Log actual error of there are no rules
                            LogMessage[LogTypes.Error]("No rules could be found in the shipping rule countries, shipping by weight won't work without them");
                        }

                        break;
                    }
                }
            }
            else
            {
                // Log actual error of there are no rules
                LogMessage[LogTypes.Error]("No country could be found in the shipping rules, shipping by weight won't work without them");
            }

            // Add a notification to the log if no shiping cost has been calculated
            if (!foundMatch && !overWeight)
            {
                LogMessage[LogTypes.Error]("NO RULE MATCH FOUND AND OVER WEIGHT IS DISABLED, SHIPPING IS FREE UNLESS STATIC COST IS SET");
            }

            if (debug)
            {
                LogMessage[LogTypes.Error]("Shipping cost is £" + shippingCostWithoutVAT);
            }

            // get shipping price property if it exists and set its value, if not then create it and set its value
            OrderProperty shippingPriceOP = order.Properties.FirstOrDefault(p => p.Alias == "shippingPriceWithoutVAT" + order.Currency.ISOCode);
            if (shippingPriceOP == null)
            {
                shippingPriceOP = new OrderProperty("shippingPriceWithoutVAT" + order.Currency.ISOCode, shippingCostWithoutVAT.ToString());
                order.AddProperty(shippingPriceOP);
            }
            else
            {
                // if the order shipping cost is same as new shipping cost, dont bother changing it
                if (Decimal.Parse(shippingPriceOP.Value) != shippingCostWithoutVAT)
                {
                    shippingPriceOP.Value = shippingCostWithoutVAT.ToString();
                }
            }

            // get total weight property if it exists and set its value, if not then create it and set its value
            OrderProperty totalWeightInKgProp = order.Properties.FirstOrDefault(p => p.Alias == "totalWeightInKg");
            if (totalWeightInKgProp == null)
            {
                totalWeightInKgProp = new OrderProperty("totalWeightInKg", totalWeightInKg.ToString());
                order.AddProperty(totalWeightInKgProp);
            }
            else
            {
                // if the order total weight is same as new total weight, dont bother changing it
                if (Decimal.Parse(totalWeightInKgProp.Value) != totalWeightInKg)
                {
                    totalWeightInKgProp.Value = totalWeightInKg.ToString();
                }
            }

            // If debug, log an error in the database
            if (debug)
            {
                LogMessage[LogTypes.Error]("Shipping method cost is: " + order.ShippingMethod.GetFeeWithoutVAT(order));

                LogMessage[LogTypes.Error]("Value before setting in ecommerce: " + (decimal)(order.ShippingMethod.GetFeeWithoutVAT(order) + shippingCostWithoutVAT));
            }

            // Set the final shipping fee into the order for use on checkout
            order.ShippingFeeWithoutVAT = (decimal)(order.ShippingMethod.GetFeeWithoutVAT(order) + shippingCostWithoutVAT);

            // If debug, log an error in the database
            if (debug)
            {
                LogMessage[LogTypes.Error]("Final value added to order: " + order.ShippingFeeWithoutVAT);
            }

            // Save the order
            order.Save();
        }
    }
}

