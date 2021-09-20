﻿module FVim.widgets

open common
open def
open theme
open Avalonia.Media.Imaging
open Avalonia.Svg
open System.IO
open Avalonia
open Avalonia.Layout
open Avalonia.Media
open System.Text

let mutable guiwidgetNamespace = -1

type SignPlacement =
    {
        line: int
        kind: SignKind
    }

type CursorHide =
| NoHide
| CursorOverlap
| CursorLineOverlap

type WidgetPlacement = 
  {
    mark: int
    widget: int
    w: int
    h: int
    opt: hashmap<obj,obj>
  }
  member this.HorizontalAlignment with get() = 
    match this.opt.TryGetValue "halign" with
    | true, x -> parseHorizontalAlignment x
    | _ -> None
    |> Option.defaultValue HorizontalAlignment.Stretch
  member this.VerticalAlignment with get() =
    match this.opt.TryGetValue "valign" with
    | true, x -> parseVerticalAlignment x
    | _ -> None
    |> Option.defaultValue VerticalAlignment.Stretch
  member this.Stretch with get() =
    match this.opt.TryGetValue "stretch" with
    | true, x -> parseStretch x
    | _ -> None
    |> Option.defaultValue Stretch.None
  member this.GetDrawingBounds (src_size: Size) (dst_bounds: Rect) =
    let halign, valign, stretch = this.HorizontalAlignment, this.VerticalAlignment, this.Stretch
    let sx, sy = dst_bounds.Width / src_size.Width , dst_bounds.Height / src_size.Height
    let scale = match stretch with
                | Stretch.Uniform -> min sx sy
                | Stretch.UniformToFill -> max sx sy
                | (*Stretch.None*) _ -> 1.0
    let scaled_size = src_size * scale
    let center = dst_bounds.Center
    let dst_l, dst_w = match halign with
                       | HorizontalAlignment.Center -> center.X - scaled_size.Width / 2.0, scaled_size.Width
                       | HorizontalAlignment.Left -> dst_bounds.Left, scaled_size.Width
                       | HorizontalAlignment.Right -> dst_bounds.Right - scaled_size.Width, scaled_size.Width
                       | (* HorizontalAlignment.Stretch *) _ -> dst_bounds.Left, dst_bounds.Width
    let dst_t, dst_h = match valign with
                       | VerticalAlignment.Center -> center.Y - scaled_size.Height / 2.0, scaled_size.Height
                       | VerticalAlignment.Top -> dst_bounds.Top, scaled_size.Height
                       | VerticalAlignment.Bottom -> dst_bounds.Bottom - scaled_size.Height, scaled_size.Height
                       | (* VerticalAlignment.Stretch *) _ -> dst_bounds.Top, dst_bounds.Height
    Rect(0.0, 0.0, src_size.Width, src_size.Height), Rect(dst_l, dst_t, dst_w, dst_h)
  member this.GetTextAttr() =
    let drawAttrs = match this.opt.TryGetValue("text-hlid") with
                    | true, (Integer32 id) -> GetDrawAttrs id
                    | true, (String semid) ->
                      match SemanticHighlightGroup.TryParse semid with
                      | true, semid -> getSemanticHighlightGroup semid
                      | _ -> GetDrawAttrs 1
                    | _ -> GetDrawAttrs 1
    let font = match this.opt.TryGetValue("text-font") with
               | true, String(fnt) -> fnt
               | _ -> theme.guifont
    let size = match this.opt.TryGetValue("text-scale") with
               | true, Float(x) -> theme.fontsize * x
               | _ -> theme.fontsize
    let fg,bg,_,attrs = drawAttrs
    let bg = Color(255uy, bg.R, bg.G, bg.B)
    let typeface = ui.GetTypeface(Rune.empty, attrs.italic, attrs.bold, font, theme.guifontwide)
    fg, bg, typeface, size
  member this.GetHideAttr() =
    match this.opt.TryGetValue("hide") with
    | true, String("cursor") -> CursorOverlap
    | true, String("cursorline") -> CursorLineOverlap
    | _ -> NoHide

let private s_no_opt = hashmap[]

let parse_placement =
  function
  | ObjArray [| Integer32 a; Integer32 b; Integer32 c; Integer32 d; |] 
    -> Some({mark = a; widget = b; w = c; h = d; opt = s_no_opt})
  | ObjArray [| Integer32 a; Integer32 b; Integer32 c; Integer32 d; :?hashmap<obj,obj> as e |] 
    -> Some({mark = a; widget = b; w = c; h = d; opt = e})
  | _ -> None


type GuiWidgetType =
| BitmapWidget of Bitmap
| VectorImageWidget of SvgImage
| PlainTextWidget of string
| UnknownWidget of mime: string * data: byte[]
| NotFound

let private widget_resources = hashmap[]
let private widget_placements = hashmap[]
let private sign_placements = hashmap[]

let loadGuiResource (id:int) (mime: string) (data: byte[]) =
    widget_resources.[id] <- 
    match mime with
    | "image/svg" ->
      let tmp = System.IO.Path.GetTempFileName()
      System.IO.File.WriteAllBytes(tmp, data)
      let img = new SvgImage()
      img.Source <- SvgSource.Load(tmp, null)
      VectorImageWidget(img)
    | x when x.StartsWith("image/") ->
      use stream = new MemoryStream(data)
      BitmapWidget(new Bitmap(stream))
    | "text/plain" ->
      let data = Encoding.UTF8.GetString(data)
      PlainTextWidget data
    | _ ->
      UnknownWidget(mime, data)

let loadGuiWidgetPlacements (buf:int) (M: WidgetPlacement[]) =
  let index = hashmap[]
  for {mark = mark} as p in M do
    index.[mark] <- p
  widget_placements.[buf] <- index

let loadSignPlacements (buf:int) (M: SignPlacement[]) =
  sign_placements.[buf] <- M

let private _no_widget_placements = hashmap[]
let getGuiWidgetPlacements (buf:int) =
  match widget_placements.TryGetValue buf with
  | true, p -> p
  | _ -> _no_widget_placements

let getGuiWidget (id: int) =
    match widget_resources.TryGetValue id with
    | true, x -> x
    | _ -> 
        // TODO make another request here
        NotFound

let private _no_sign_placements = [||]
let getSignPlacements (buf:int) =
  match sign_placements.TryGetValue buf with
  | true, p -> p
  | _ -> _no_sign_placements

