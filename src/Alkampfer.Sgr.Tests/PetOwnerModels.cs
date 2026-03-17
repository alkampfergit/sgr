using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;

namespace Alkampfer.Sgr.Tests;

public class PetOwner
{
    [JsonProperty("name")]
    [Required]
    public string Name { get; set; } = default!;

    [JsonProperty("surname")]
    [Required]
    public string Surname { get; set; } = default!;

    [JsonProperty("address")]
    [Required]
    public string Address { get; set; } = default!;

    [JsonProperty("pet")]
    [Required]
    public Pet Pet { get; set; } = default!;
}

public abstract class Pet
{
    [JsonProperty("type")]
    [Required]
    public abstract string Type { get; }
}

public class Dog : Pet
{
    [JsonProperty("breed")]
    [Required]
    public string Breed { get; set; } = default!;

    [JsonProperty("barkVolume")]
    public int BarkVolume { get; set; }

    public override string Type => "dog";
}

public class Cat : Pet
{
    [JsonProperty("color")]
    [Required]
    public string Color { get; set; } = default!;

    public override string Type => "cat";
}
