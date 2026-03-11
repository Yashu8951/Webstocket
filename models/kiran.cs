using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace WebApplication1.Models
{
    [Table("kiran")]
    public class Kiran
    {
        [Key]
        [Column("item_id")]
        public int ItemId { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("status")]
        public int Status { get; set; }
    }
}