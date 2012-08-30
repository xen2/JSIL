using System;

public interface I<T> {
    void Method(T a);
    //void Method(ref T a);
}

public class A<T> : I<T> {
    public void Method(T a) {
        Console.WriteLine("Test(T)");
    }
    public void Method(ref T a) {
        Console.WriteLine("Test(ref T)");
    }

    public void Test2() {
        
    }
}


public static class Program {
    public static void Main (string[] args) {
        ((I<int>)new A<int>()).Method(1);
        ((I<string>)new A<string>()).Method("a");
    }
}