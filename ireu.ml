open Parse_csv

let filename = "nsf_reusites.csv";;
let () =
  let records = Parse_csv.parse_csv filename in
  List.iter (fun r -> print_endline (r.reu_url)) records
;;
