using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlcManagementService.Models
{
    public class AutoRunBill
    {
        public string PlcId { get; set; }  //需要下架的PLC
        public string BillID { get; set; }    //单据类别，对应 [BillDetail]的BillID
        public string BillNo { get; set; }    //单据编号，对应 [BillDetail]的BillNO
        public int ITM { get; set; }          //单据编号，对应 [BillDetail]的ITM
        public string Tray { get; set; }      //货箱编号
        public int Position { get; set; }     //对应需要下架PLC的储位编号
        public int Shelf { get; set; }       //对应需要下架PLC的货架编号
        public int PLCLocationCode { get; set; }       //0外装载点，1表示内装载点
        public int OperationType { get; set; }       //0表示上架，1表示下架

    }
}
