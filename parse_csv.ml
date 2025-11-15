type record = { 
    reu_name : string; 
    reu_url : string 
}

let to_record reu = 
    match reu with
    | name :: url :: _ -> Some { reu_name = name; reu_url = url }
    | _ -> None

let parse_csv filename : record list =
  let ic = open_in filename in
  let csv_ic =
    Csv.of_channel ~separator:',' ~strip:true ~has_header:true ic
  in
  let rows = Csv.input_all csv_ic in
  Csv.close_in csv_ic;
  close_in ic;
  List.filter_map to_record rows
;;
