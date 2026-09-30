using System.Collections.Generic;

namespace SCSSdkClient.Object {
    public partial class SCSTelemetry {
        public CarJob CarJobValues { get; internal set; } = new CarJob();
        public Sdk115Channels Sdk115ChannelValues { get; internal set; } = new Sdk115Channels();
        public GenericBlock BusJobConfig { get; internal set; } = new GenericBlock();
        public GenericBlock OtherConfig { get; internal set; } = new GenericBlock();
        public GenericBlock OtherGameplayEvent { get; internal set; } = new GenericBlock();

        public class CarJob {
            public bool OnJob { get; internal set; }
            public bool Finished { get; internal set; }
            public bool Cancelled { get; internal set; }
            public bool Delivered { get; internal set; }

            public string CargoId { get; internal set; }
            public string Cargo { get; internal set; }
            public uint UnitCount { get; internal set; }
            public string CityDestinationId { get; internal set; }
            public string CityDestination { get; internal set; }
            public string CompanyDestinationId { get; internal set; }
            public string CompanyDestination { get; internal set; }
            public string CitySourceId { get; internal set; }
            public string CitySource { get; internal set; }
            public string CompanySourceId { get; internal set; }
            public string CompanySource { get; internal set; }
            public ulong Income { get; internal set; }
            public Time DeliveryTime { get; internal set; } = new Time();
            public Frequency RemainingDeliveryTime { get; internal set; } = new Frequency();
            public uint PlannedDistanceKm { get; internal set; }
            public string Market { get; internal set; }
            public bool CustomerPrioCargoHandling { get; internal set; }
            public bool CustomerPrioTime { get; internal set; }
            public bool CustomerPrioVehicleAppearance { get; internal set; }
            public Time StartingTime { get; internal set; } = new Time();
            public Time FinishedTime { get; internal set; } = new Time();

            public long DeliveredRevenue { get; internal set; }
            public int DeliveredEarnedXp { get; internal set; }
            public float DeliveredCargoDamage { get; internal set; }
            public float DeliveredVehicleDamage { get; internal set; }
            public float DeliveredDistanceKm { get; internal set; }
            public Time DeliveredArrivalTime { get; internal set; } = new Time();
            public long CancelledPenalty { get; internal set; }
        }

        public class Sdk115Channels {
            public int MandatoryBreak { get; internal set; }
            public bool MandatoryBreakRegistered { get; internal set; }
            public bool MandatoryBreakHasValue { get; internal set; }
            public float BusJobAverageSatisfaction { get; internal set; }
            public bool BusJobAverageSatisfactionRegistered { get; internal set; }
            public bool BusJobAverageSatisfactionHasValue { get; internal set; }
        }

        /// <summary>
        ///     A configuration or gameplay event the plugin has no typed fields for, copied as text
        ///     (plugin revision 13+). BusJobConfig holds the latest "bus_job" configuration (not yet
        ///     documented by SCS), OtherConfig the latest configuration with any other unknown id and
        ///     OtherGameplayEvent the latest unknown gameplay event (e.g. bus_job.completed).
        ///     Each block only keeps its most recent update.
        /// </summary>
        public class GenericBlock {
            /// <summary>
            ///     Incremented on every update, so a new event is detectable even when it repeats the previous one
            /// </summary>
            public uint Sequence { get; internal set; }

            /// <summary>
            ///     Attributes the game sent; more than Attributes.Count when the block ran out of slots
            ///     (32 for bus_job, 8 for other configurations, 12 for events)
            /// </summary>
            public uint Count { get; internal set; }

            public string Id { get; internal set; }
            public List<GenericAttribute> Attributes { get; internal set; } = new List<GenericAttribute>();
        }

        /// <summary>
        ///     Index is uint.MaxValue for non-array attributes and Type is the SDK scs_value_type_t.
        ///     Value is text: integers in decimal, bools as true/false, floats with '.' decimals,
        ///     vectors "x y z", eulers "heading pitch roll", placements "x y z heading pitch roll".
        ///     Names and values are cut at 63 characters.
        /// </summary>
        public class GenericAttribute {
            public uint Index { get; internal set; }
            public uint Type { get; internal set; }
            public string Name { get; internal set; }
            public string Value { get; internal set; }
        }
    }
}
