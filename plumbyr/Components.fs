namespace WindowsCleaner

open Avalonia
open Avalonia.FuncUI.DSL
open Avalonia.Media
open Avalonia.Layout

module Components =

    let categoryCard (name: string) (size: string) (isSelected: bool) (onToggle: bool -> unit) =
        Border.create [
            Border.background (if isSelected then SolidColorBrush.Parse("#2d2d2d") else SolidColorBrush.Parse("#1e1e1e"))
            Border.cornerRadius 10.0
            Border.padding 15.0
            Border.margin 5.0
            Border.borderThickness 1.0
            Border.borderBrush (if isSelected then Brushes.DodgerBlue else Brushes.Transparent)
            Border.child (
                StackPanel.create [
                    StackPanel.spacing 5.0
                    StackPanel.children [
                        CheckBox.create [
                            CheckBox.content name
                            CheckBox.isChecked isSelected
                            CheckBox.onChecked (fun _ -> onToggle true)
                            CheckBox.onUnchecked (fun _ -> onToggle false)
                        ]
                        TextBlock.create [
                            TextBlock.text (sprintf "Est. Size: %s" size)
                            TextBlock.fontSize 10.0
                            TextBlock.foreground Brushes.Gray
                            TextBlock.margin (30.0, 0.0, 0.0, 0.0)
                        ]
                    ]
                ]
            )
        ]

    let statsBox (label: string) (value: string) (icon: string) =
        Border.create [
            Border.background (SolidColorBrush.Parse "#252525")
            Border.cornerRadius 12.0
            Border.padding 20.0
            Border.child (
                StackPanel.create [
                    StackPanel.children [
                        TextBlock.create [
                            TextBlock.text label
                            TextBlock.fontSize 10.0
                            TextBlock.foreground Brushes.Gray
                            TextBlock.fontWeight FontWeight.Bold
                        ]
                        TextBlock.create [
                            TextBlock.text value
                            TextBlock.fontSize 24.0
                            TextBlock.fontWeight FontWeight.ExtraBold
                            TextBlock.foreground Brushes.White
                            TextBlock.margin (0.0, 5.0)
                        ]
                    ]
                ]
            )
        ]
