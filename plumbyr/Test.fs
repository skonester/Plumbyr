namespace WindowsCleaner

open Avalonia.FuncUI.DSL

module Test =
    let x = 
        Button.create [
            Button.content "Test"
        ]

    let y =
        DockPanel.create [
            DockPanel.children [
                Button.create [ Button.content "Inner" ]
            ]
        ]
